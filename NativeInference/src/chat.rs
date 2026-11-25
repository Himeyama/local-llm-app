//! A deliberately small Qwen ChatML adapter and incremental output parser.
//! Images are rejected explicitly until the vision encoder is implemented.
use crate::{Result, tokenizer::Tokenizer};
use serde_json::{Value, json};

pub struct Prompt {
    pub text: String,
    pub max_tokens: usize,
    pub stream: bool,
    /// Stable message prefix, before the generated assistant header. BPE can
    /// merge that header's newline with leading whitespace in a stored reply.
    pub cache_boundary: usize,
}
impl Prompt {
    pub fn from_request(request: &Value) -> Result<Self> {
        let messages = request["messages"]
            .as_array()
            .filter(|m| !m.is_empty())
            .ok_or("messages must be a nonempty array")?;
        let max_tokens = request
            .get("max_tokens")
            .or_else(|| request.get("max_completion_tokens"))
            .map(|v| v.as_u64().ok_or("Invalid max_tokens"))
            .transpose()?
            .unwrap_or(256);
        if !(1..=16384).contains(&max_tokens) {
            return Err("max_tokens must be 1..16384".into());
        }
        let effort = request["reasoning_effort"].as_str().unwrap_or("none");
        if !matches!(effort, "none" | "low" | "medium" | "xhigh") {
            return Err("Unsupported reasoning_effort".into());
        }
        if request.get("n").is_some_and(|n| n.as_u64() != Some(1)) {
            return Err("Only n=1 is supported".into());
        }
        if request.get("stop").is_some_and(|s| !s.is_null()) {
            return Err("Custom stop sequences are not yet supported".into());
        }
        if request
            .get("temperature")
            .is_some_and(|t| t.as_f64() != Some(0.0))
        {
            return Err("This decoder currently supports temperature=0 only".into());
        }
        let mut text = String::new();
        if let Some(tools) = request
            .get("tools")
            .and_then(Value::as_array)
            .filter(|t| !t.is_empty())
        {
            if request
                .get("tool_choice")
                .and_then(Value::as_str)
                .is_some_and(|c| c != "auto")
            {
                return Err("Only automatic tool selection is supported".into());
            }
            let functions = tools
                .iter()
                .map(|tool| {
                    if tool["type"].as_str() != Some("function") || !tool["function"].is_object() {
                        return Err("Invalid function tool".into());
                    }
                    Ok(tool["function"].clone())
                })
                .collect::<Result<Vec<_>>>()?;
            text.push_str("<|im_start|>system\n# Tools\n\nYou have access to the following functions:\n\n<tools>\n");
            for function in functions {
                text.push_str(&serde_json::to_string(&function)?);
                text.push('\n');
            }
            text.push_str("</tools>\n\nIf you choose to call a function ONLY reply in the following format with NO suffix:\n\n<tool_call>\n<function=example_function_name>\n<parameter=example_parameter_1>\nvalue_1\n</parameter>\n<parameter=example_parameter_2>\nvalue_2\n</parameter>\n</function>\n</tool_call>\n\n<IMPORTANT>\n- Function calls MUST use an inner <function=...></function> block inside <tool_call></tool_call>.\n- Required parameters MUST be specified.\n- You may reason before the function call, but not after it.\n- If no function is available, answer normally.\n</IMPORTANT><|im_end|>\n");
        }
        for message in messages {
            let role = message["role"].as_str().ok_or("Missing message role")?;
            if !matches!(role, "system" | "user" | "assistant" | "tool") {
                return Err("Unsupported message role".into());
            }
            let content = match &message["content"] {
                Value::String(s) => s.clone(),
                Value::Null => String::new(),
                Value::Array(parts) => {
                    let mut result = String::new();
                    for part in parts {
                        if part["type"] != "text" {
                            return Err(
                                "Image/audio input is not implemented by the native decoder".into(),
                            );
                        }
                        result.push_str(part["text"].as_str().ok_or("Invalid text part")?);
                    }
                    result
                }
                _ => return Err("Invalid message content".into()),
            };
            text.push_str("<|im_start|>");
            text.push_str(if role == "tool" { "user" } else { role });
            text.push('\n');
            if role == "tool" {
                text.push_str("<tool_response>\n");
            }
            text.push_str(&content);
            if role == "tool" {
                text.push_str("\n</tool_response>");
            }
            if let Some(calls) = message["tool_calls"].as_array() {
                for call in calls {
                    let arguments: Value = serde_json::from_str(
                        call["function"]["arguments"]
                            .as_str()
                            .ok_or("Invalid prior tool arguments")?,
                    )?;
                    let name = call["function"]["name"]
                        .as_str()
                        .ok_or("Invalid prior function name")?;
                    let arguments = arguments
                        .as_object()
                        .ok_or("Invalid prior tool arguments")?;
                    text.push_str(&format!("\n<tool_call>\n<function={name}>\n"));
                    for (key, value) in arguments {
                        let value = match value {
                            Value::String(s) => s.clone(),
                            _ => serde_json::to_string(value)?,
                        };
                        text.push_str(&format!("<parameter={key}>\n{value}\n</parameter>\n"));
                    }
                    text.push_str("</function>\n</tool_call>");
                }
            }
            text.push_str("<|im_end|>\n");
        }
        let cache_boundary = text.len();
        text.push_str("<|im_start|>assistant\n");
        text.push_str("<think>\n");
        if effort == "none" {
            text.push_str("\n</think>\n\n");
        }
        Ok(Self {
            text,
            max_tokens: max_tokens as usize,
            stream: request["stream"].as_bool().unwrap_or(false),
            cache_boundary,
        })
    }
}

#[derive(Default)]
pub struct Output {
    pending: String,
    reasoning: bool,
    tool: bool,
    tool_index: usize,
    request_id: String,
    tools: Value,
    pub content: String,
    pub thoughts: String,
    pub calls: Vec<Value>,
}
impl Output {
    pub fn new(reasoning: bool, request_id: &str) -> Self {
        Self {
            reasoning,
            request_id: request_id.to_owned(),
            ..Self::default()
        }
    }
    pub fn set_tools(&mut self, tools: Value) {
        self.tools = tools;
    }
    /// Returns OpenAI delta objects. A trailing partial tag stays buffered so
    /// markup split across tokenizer pieces never leaks into visible content.
    pub fn push(&mut self, text: &str, finished: bool) -> Result<Vec<Value>> {
        self.pending.push_str(text);
        let mut deltas = Vec::new();
        loop {
            if self.tool {
                let Some(end) = self.pending.find("</tool_call>") else {
                    if finished {
                        return Err(
                            "Model returned an incomplete tool call; retry the request".into()
                        );
                    }
                    break;
                };
                let raw = self.pending[..end].trim();
                let call = parse_tool_call(raw, &self.tools);
                let valid = call.as_ref().is_some_and(|call| {
                    call["name"].as_str().is_some_and(|name| !name.is_empty())
                        && call["arguments"].is_object()
                });
                if !valid {
                    return Err("Model returned a malformed tool call; retry the request".into());
                }
                let call = call.expect("validated tool call");
                let name = call["name"].as_str().expect("validated function name");
                let function =
                    json!({"name":name,"arguments":serde_json::to_string(&call["arguments"])?});
                let id = format!("call_{}_{}", self.request_id, self.tool_index);
                self.calls
                    .push(json!({"id":id,"type":"function","function":function}));
                deltas.push(json!({"tool_calls":[{"index":self.tool_index,"id":id,"type":"function","function":function}]}));
                self.tool_index += 1;
                self.pending.drain(..end + "</tool_call>".len());
                self.tool = false;
                continue;
            }
            let tags = ["<think>", "</think>", "<tool_call>"];
            let next = tags
                .iter()
                .filter_map(|tag| self.pending.find(tag).map(|i| (i, *tag)))
                .min_by_key(|p| p.0);
            if let Some((offset, tag)) = next {
                let segment = self.pending[..offset].to_owned();
                self.emit(&segment, &mut deltas);
                self.pending.drain(..offset + tag.len());
                match tag {
                    "<think>" => self.reasoning = true,
                    "</think>" => self.reasoning = false,
                    _ => self.tool = true,
                }
                continue;
            }
            let keep = if finished {
                0
            } else {
                (1..=self.pending.len().min(12))
                    .filter(|&n| {
                        self.pending.is_char_boundary(self.pending.len() - n)
                            && tags
                                .iter()
                                .any(|tag| tag.starts_with(&self.pending[self.pending.len() - n..]))
                    })
                    .max()
                    .unwrap_or(0)
            };
            let end = self.pending.len() - keep;
            let segment = self.pending[..end].to_owned();
            self.pending.drain(..end);
            self.emit(&segment, &mut deltas);
            break;
        }
        Ok(deltas)
    }
    fn emit(&mut self, text: &str, deltas: &mut Vec<Value>) {
        if text.is_empty() {
            return;
        }
        if self.reasoning {
            self.thoughts.push_str(text);
            deltas.push(json!({"reasoning_content":text}));
        } else {
            self.content.push_str(text);
            deltas.push(json!({"content":text}));
        }
    }
}

// Some models omit the key for the function name and place parameters at the
// top level: {"read_file", "path":"a"}. Recover only this unambiguous form.
fn parse_tool_call(raw: &str, tools: &Value) -> Option<Value> {
    if let Some(rest) = raw.strip_prefix("<function=") {
        let (name, rest) = rest.split_once('>')?;
        if name.is_empty() || name.contains(['<', '\n', '\r']) {
            return None;
        }
        let mut rest = rest.strip_suffix("</function>")?.trim();
        let schema = tools
            .as_array()
            .and_then(|tools| tools.iter().find(|tool| tool["function"]["name"] == name));
        let mut arguments = serde_json::Map::new();
        while !rest.is_empty() {
            rest = rest.strip_prefix("<parameter=")?;
            let (key, body) = rest.split_once('>')?;
            if key.is_empty() || key.contains(['<', '\n', '\r']) || arguments.contains_key(key) {
                return None;
            }
            let (value, tail) = body.split_once("</parameter>")?;
            let value = value
                .strip_prefix('\n')
                .unwrap_or(value)
                .strip_suffix('\n')
                .unwrap_or(value.strip_prefix('\n').unwrap_or(value));
            let kind = schema.and_then(|tool| {
                tool["function"]["parameters"]["properties"][key]["type"].as_str()
            });
            let parsed = match kind {
                Some("string") | None => Value::String(value.to_owned()),
                Some(_) => serde_json::from_str(value).ok()?,
            };
            arguments.insert(key.to_owned(), parsed);
            rest = tail.trim();
        }
        return Some(json!({"name":name,"arguments":arguments}));
    }
    if let Ok(call) = serde_json::from_str(raw) {
        return Some(call);
    }
    let rest = raw.strip_prefix('{')?.trim_start();
    let mut stream = serde_json::Deserializer::from_str(rest).into_iter::<String>();
    let name = stream.next()?.ok()?;
    let rest = rest[stream.byte_offset()..]
        .trim_start()
        .strip_prefix(',')?;
    let arguments: Value = serde_json::from_str(&format!("{{{rest}")).ok()?;
    arguments
        .is_object()
        .then(|| json!({"name": name, "arguments": arguments}))
}

pub fn is_end(tokenizer: &Tokenizer, token: u32) -> Result<bool> {
    Ok(token == tokenizer.eos
        || matches!(tokenizer.token_text(token)?, "<|im_end|>" | "<|endoftext|>"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn stable_prefix_survives_assistant_reasoning_normalization() {
        let first=Prompt::from_request(&json!({"messages":[{"role":"system","content":"test"},{"role":"user","content":"hello"}],"reasoning_effort":"none"})).unwrap();
        let next=Prompt::from_request(&json!({"messages":[{"role":"system","content":"test"},{"role":"user","content":"hello"},{"role":"assistant","content":"answer"},{"role":"user","content":"next"}],"reasoning_effort":"xhigh"})).unwrap();
        assert!(next.text.starts_with(&first.text[..first.cache_boundary]));
        assert!(first.text[first.cache_boundary..].starts_with("<|im_start|>assistant\n<think>"));
    }
    #[test]
    fn fragmented_thoughts_and_tool_json() {
        let mut out = Output::new(false, "test");
        for chunk in [
            "<thi",
            "nk>plan</thi",
            "nk>answer<tool_",
            "call>{\"name\":\"read_file\",\"arguments\":{\"path\":\"a\"}}</tool_",
            "call>",
        ] {
            out.push(chunk, false).unwrap();
        }
        out.push("", true).unwrap();
        assert_eq!(out.thoughts, "plan");
        assert_eq!(out.content, "answer");
        assert_eq!(out.calls[0]["function"]["name"], "read_file");
    }
    #[test]
    fn incomplete_tools_are_not_executable() {
        let mut out = Output::default();
        assert!(out.push("before<tool_call>{\"name\":", true).is_err());
        assert_eq!(out.content, "before");
        assert!(out.calls.is_empty());
    }
    #[test]
    fn missing_name_key_is_recovered() {
        let mut out = Output::new(false, "test");
        out.push(
            "<tool_call>{\"list_directory\",\"path\":\".\"}</tool_call>",
            true,
        )
        .unwrap();
        assert_eq!(out.calls.len(), 1);
        assert_eq!(out.calls[0]["function"]["name"], "list_directory");
        assert_eq!(out.calls[0]["function"]["arguments"], "{\"path\":\".\"}");
    }
    #[test]
    fn invalid_tool_shape_is_not_executed() {
        let mut out = Output::default();
        assert!(
            out.push(
                "<tool_call>{\"name\":\"bad\",\"arguments\":[]}</tool_call>",
                true
            )
            .is_err()
        );
        assert!(out.calls.is_empty());
        assert!(out.content.is_empty());
    }
    #[test]
    fn nested_malformed_calls_are_rejected() {
        let mut out = Output::default();
        assert!(out.push("<tool_call>{\"list_directory\",\"path\":\".\"}<tool_call>{\"web_read\",\"url\":\"x\"}</tool_call>", true).is_err());
        assert!(out.calls.is_empty());
        assert!(out.content.is_empty());
    }
    #[test]
    fn qwen_xml_tool_call_uses_parameter_types() {
        let mut out = Output::new(false, "test");
        out.set_tools(json!([{"function":{"name":"read", "parameters":{"properties":{"path":{"type":"string"},"limit":{"type":"integer"}}}}}]));
        for part in [
            "<tool_call>\n<function=read>\n<parameter=path>\n",
            "true\n</parameter>\n<parameter=limit>\n5\n</parameter>\n</function>\n</tool_call>",
        ] {
            out.push(part, false).unwrap();
        }
        let arguments: Value =
            serde_json::from_str(out.calls[0]["function"]["arguments"].as_str().unwrap()).unwrap();
        assert_eq!(arguments, json!({"path":"true", "limit":5}));
    }
    #[test]
    fn images_and_invalid_limits_are_rejected() {
        assert!(
            Prompt::from_request(
                &json!({"messages":[{"role":"user","content":[{"type":"image_url"}]}]})
            )
            .is_err()
        );
        assert!(
            Prompt::from_request(
                &json!({"messages":[{"role":"user","content":"hi"}],"max_tokens":0})
            )
            .is_err()
        );
    }
}

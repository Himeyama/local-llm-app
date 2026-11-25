//! Local HTTP API. Socket handlers answer health checks independently while the
//! single GPU worker owns the model. A bounded queue prevents parallel GPU jobs.
use crate::{
    Result,
    chat::{Output, Prompt, is_end},
    gguf::Gguf,
    model::Model,
    tokenizer::Tokenizer,
};
use serde_json::{Value, json};
use std::{
    io::{self, Cursor, Read, Write},
    net::{TcpListener, TcpStream},
    path::Path,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
        mpsc::{self, SyncSender},
    },
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

const MAX_HEADER: usize = 16 * 1024;
const MAX_BODY: usize = 16 * 1024 * 1024;
struct Request {
    method: String,
    path: String,
    body: Vec<u8>,
}
struct Job {
    socket: TcpStream,
    request: Value,
    prompt: Prompt,
    stream_started: bool,
    queued_at: Instant,
}

pub fn serve(path: &Path, sdk: &Path, host: &str, port: u16, capacity: usize) -> Result<()> {
    if !matches!(host, "127.0.0.1" | "0.0.0.0") {
        return Err("Host must be 127.0.0.1 or 0.0.0.0".into());
    }
    // Bind before loading many GiB so an occupied port fails immediately.
    let listener = TcpListener::bind((host, port))?;
    let file = Gguf::open(path)?;
    let tokenizer = Tokenizer::from_gguf(&file)?;
    let mut model = Model::load(&file, sdk, capacity)?;
    eprintln!(
        "ROCm bindings: rocmrc / prefill batch {}{} / {}",
        model.prefill_batch_size(),
        if model.prefill_batch_size() > 8 {
            " / rocBLAS"
        } else {
            " / HIP kernels"
        },
        model.prefill_precision()
    );
    let alias = path
        .file_stem()
        .and_then(|n| n.to_str())
        .ok_or("Invalid model filename")?
        .to_owned();
    let props = json!({"model_alias":alias,"model_path":path.canonicalize()?.to_string_lossy(),"backend":"native-rocm", "total_slots":1,
        "default_generation_settings":{"n_ctx":capacity,"params":{"temperature":0.0}},"modalities":{"vision":false,"audio":false},
        "capabilities":{"chat_completions":true,"tools":true,"responses":false,"messages":false,"mtp":false}});
    let (sender, receiver) = mpsc::sync_channel::<Job>(1);
    let connections = Arc::new(AtomicUsize::new(0));
    std::thread::spawn(move || {
        for socket in listener.incoming() {
            let Ok(socket) = socket else { break };
            if connections.fetch_add(1, Ordering::Relaxed) >= 16 {
                connections.fetch_sub(1, Ordering::Relaxed);
                continue;
            }
            let sender = sender.clone();
            let props = props.clone();
            let connections = connections.clone();
            std::thread::spawn(move || {
                struct Active(Arc<AtomicUsize>);
                impl Drop for Active {
                    fn drop(&mut self) {
                        self.0.fetch_sub(1, Ordering::Relaxed);
                    }
                }
                let _active = Active(connections);
                if let Err(error) = handle(socket, &sender, &props) {
                    // GUI readiness probes can time out while the model loads.
                    // Peer cancellation is normal, not a server failure.
                    if !is_disconnect(error.as_ref()) {
                        eprintln!("HTTP connection: {error}");
                    }
                }
            });
        }
    });
    eprintln!("server is listening on http://{host}:{port} (native ROCm, text only)");
    for mut job in receiver {
        if let Err(error) = generate(&mut model, &tokenizer, &alias, &mut job) {
            if !is_disconnect(error.as_ref()) {
                eprintln!("generation: {error}");
            }
            if job.stream_started {
                let _ = event(
                    &mut job.socket,
                    &json!({"error":{"message":error.to_string()}}),
                );
            } else {
                let _ = reply(
                    &mut job.socket,
                    500,
                    &json!({"error":{"message":error.to_string()}}),
                );
            }
            // Once SSE headers have been sent, errors belong to the event stream.
            // Closing the socket without [DONE] also preserves client retry behavior.
        }
    }
    Ok(())
}
fn is_disconnect(error: &(dyn std::error::Error + 'static)) -> bool {
    error.downcast_ref::<io::Error>().is_some_and(|error| {
        matches!(
            error.kind(),
            io::ErrorKind::ConnectionAborted
                | io::ErrorKind::ConnectionReset
                | io::ErrorKind::BrokenPipe
                | io::ErrorKind::UnexpectedEof
        )
    })
}
fn handle(mut socket: TcpStream, sender: &SyncSender<Job>, props: &Value) -> Result<()> {
    socket.set_read_timeout(Some(Duration::from_secs(15)))?;
    socket.set_write_timeout(Some(Duration::from_secs(30)))?;
    let request = match read_request(&mut socket) {
        Ok(r) => r,
        Err(error) if is_disconnect(error.as_ref()) => return Ok(()),
        Err(error) => {
            return reply(
                &mut socket,
                400,
                &json!({"error":{"message":error.to_string()}}),
            );
        }
    };
    match (request.method.as_str(), request.path.as_str()) {
        ("GET", "/health") => reply(
            &mut socket,
            200,
            &json!({"status":"ok","backend":"native-rocm"}),
        ),
        ("GET", "/props") => reply(&mut socket, 200, props),
        ("GET", "/v1/models") => reply(
            &mut socket,
            200,
            &json!({"object":"list","data":[{"id":props["model_alias"],"object":"model","owned_by":"local"}]}),
        ),
        ("POST", "/v1/chat/completions") => {
            let request: Value = match serde_json::from_slice(&request.body) {
                Ok(v) => v,
                Err(e) => {
                    return reply(
                        &mut socket,
                        400,
                        &json!({"error":{"message":e.to_string()}}),
                    );
                }
            };
            if request
                .get("model")
                .is_some_and(|m| m != &props["model_alias"])
            {
                return reply(
                    &mut socket,
                    404,
                    &json!({"error":{"message":"Requested model is not loaded"}}),
                );
            }
            let prompt = match Prompt::from_request(&request) {
                Ok(p) => p,
                Err(e) => {
                    return reply(
                        &mut socket,
                        400,
                        &json!({"error":{"message":e.to_string()}}),
                    );
                }
            };
            let job = Job {
                socket,
                request,
                prompt,
                stream_started: false,
                queued_at: Instant::now(),
            };
            match sender.try_send(job) {
                Ok(()) => Ok(()),
                Err(error) => {
                    let mut job = match error {
                        mpsc::TrySendError::Full(job) | mpsc::TrySendError::Disconnected(job) => {
                            job
                        }
                    };
                    reply(
                        &mut job.socket,
                        503,
                        &json!({"error":{"message":"Inference queue is full"}}),
                    )
                }
            }
        }
        ("POST", "/v1/responses" | "/v1/messages") => reply(
            &mut socket,
            501,
            &json!({"error":{"message":"Native backend currently supports /v1/chat/completions. Responses and Messages adapters are not implemented."}}),
        ),
        _ => reply(
            &mut socket,
            404,
            &json!({"error":{"message":"Unknown endpoint"}}),
        ),
    }
}
fn generate(model: &mut Model, tokenizer: &Tokenizer, alias: &str, job: &mut Job) -> Result<()> {
    let started = Instant::now();
    let queue_seconds = job.queued_at.elapsed().as_secs_f64();
    let ids = tokenizer.encode(&job.prompt.text)?;
    if ids.is_empty() || ids.len().saturating_add(job.prompt.max_tokens) > model.capacity {
        return reply(
            &mut job.socket,
            400,
            &json!({"error":{"code":"context_length_exceeded","message":"Prompt plus max_tokens exceeds the context capacity"}}),
        );
    }
    let stable_prefix = tokenizer.encode(&job.prompt.text[..job.prompt.cache_boundary])?;
    let checkpoint_at = ids
        .iter()
        .zip(&stable_prefix)
        .take_while(|(a, b)| a == b)
        .count();
    let tokenize_seconds = started.elapsed().as_secs_f64();
    let prefill_started = Instant::now();
    let reused = model.prepare_prompt(&ids)?;
    eprintln!(
        "prefill: prompt {} tokens / reused {} / remaining {} / tokenize {:.3}s / queue {:.3}s / batch {}",
        ids.len(),
        reused,
        ids.len() - reused,
        tokenize_seconds,
        queue_seconds,
        model.prefill_batch_size()
    );
    let created = SystemTime::now().duration_since(UNIX_EPOCH)?.as_secs();
    let nonce = SystemTime::now().duration_since(UNIX_EPOCH)?.as_nanos();
    let id = format!("chatcmpl-native-{nonce}");
    if job.prompt.stream {
        job.socket.write_all(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n")?;
        job.stream_started = true;
        event(
            &mut job.socket,
            &json!({"id":id,"object":"chat.completion.chunk","created":created,"model":alias,"choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}),
        )?;
    }
    let mut logits = if reused == ids.len() {
        model.cached_logits()
    } else {
        None
    };
    if reused == checkpoint_at && checkpoint_at > 0 {
        model.save_checkpoint()?;
    }
    let mut last_progress = Instant::now();
    let mut cursor = reused;
    while cursor < ids.len() {
        // Do not cross the checkpoint boundary: its recurrent snapshot must
        // correspond to exactly the stable conversation prefix.
        let boundary = if cursor < checkpoint_at {
            checkpoint_at
        } else {
            ids.len()
        };
        let end = (cursor + model.prefill_batch_size()).min(boundary);
        // Heartbeats also detect disconnected clients during a long prefill.
        if job.prompt.stream {
            job.socket.write_all(b": prefill\n\n")?;
        }
        logits = model.forward_tokens(&ids[cursor..end], end == ids.len())?;
        cursor = end;
        if cursor == checkpoint_at && checkpoint_at > 0 {
            model.save_checkpoint()?;
        }
        if last_progress.elapsed() >= Duration::from_secs(2) {
            eprintln!(
                "prefill progress: {} / {} tokens ({:.2}s)",
                cursor,
                ids.len(),
                prefill_started.elapsed().as_secs_f64()
            );
            last_progress = Instant::now();
        }
    }
    let prefill_seconds = prefill_started.elapsed().as_secs_f64();
    eprintln!(
        "prefill complete: {} new tokens in {:.3}s",
        ids.len() - reused,
        prefill_seconds
    );
    let decode_started = Instant::now();
    let mut output = Output::new(
        job.request["reasoning_effort"].as_str().unwrap_or("none") != "none",
        &id,
    );
    output.set_tools(job.request["tools"].clone());
    let mut pending_bytes = Vec::new();
    let mut generated = 0;
    let mut decode_evaluations = 0;
    let mut stopped = false;
    for index in 0..job.prompt.max_tokens {
        let scores = logits.take().ok_or("Missing output logits")?;
        let token = scores
            .iter()
            .enumerate()
            .max_by(|a, b| a.1.total_cmp(b.1))
            .ok_or("Empty logits")?
            .0 as u32;
        generated += 1;
        if is_end(tokenizer, token)? {
            stopped = true;
            break;
        }
        pending_bytes.extend(tokenizer.decode_bytes(&[token])?);
        let valid = match std::str::from_utf8(&pending_bytes) {
            Ok(_) => pending_bytes.len(),
            Err(e) if e.error_len().is_none() => e.valid_up_to(),
            Err(_) => return Err("Model emitted invalid UTF-8".into()),
        };
        let text = String::from_utf8(pending_bytes.drain(..valid).collect())?;
        for delta in output.push(&text, false)? {
            if job.prompt.stream {
                event(
                    &mut job.socket,
                    &json!({"id":id,"object":"chat.completion.chunk","created":created,"model":alias,"choices":[{"index":0,"delta":delta,"finish_reason":null}]}),
                )?;
            }
        }
        if index + 1 < job.prompt.max_tokens {
            logits = model.forward(token, true)?;
            decode_evaluations += 1;
        }
    }
    for delta in output.push(&String::from_utf8_lossy(&pending_bytes), true)? {
        if job.prompt.stream {
            event(
                &mut job.socket,
                &json!({"id":id,"object":"chat.completion.chunk","created":created,"model":alias,"choices":[{"index":0,"delta":delta,"finish_reason":null}]}),
            )?;
        }
    }
    let finish = if !stopped {
        "length"
    } else if !output.calls.is_empty() {
        "tool_calls"
    } else {
        "stop"
    };
    let usage = json!({"prompt_tokens":ids.len(),"completion_tokens":generated,"total_tokens":ids.len()+generated,
        "prompt_tokens_details":{"cached_tokens":reused}});
    let decode_seconds = decode_started.elapsed().as_secs_f64();
    let (allocations, reuses, cached_bytes) = model.allocation_stats();
    eprintln!(
        "timings: prompt {} / reused {} / prefill {:.3}s / generated {} tokens / decode {} evaluations in {:.3}s ({:.2} evals/s) / total {:.3}s / GPU new {} reused {} cached {:.1} MiB",
        ids.len(),
        reused,
        prefill_seconds,
        generated,
        decode_evaluations,
        decode_seconds,
        decode_evaluations as f64 / decode_seconds.max(1e-9),
        started.elapsed().as_secs_f64(),
        allocations,
        reuses,
        cached_bytes as f64 / 1048576.0
    );
    if job.prompt.stream {
        event(
            &mut job.socket,
            &json!({"id":id,"object":"chat.completion.chunk","created":created,"model":alias,"choices":[{"index":0,"delta":{},"finish_reason":finish}]}),
        )?;
        if job.request["stream_options"]["include_usage"] == true {
            event(
                &mut job.socket,
                &json!({"id":id,"object":"chat.completion.chunk","created":created,"model":alias,"choices":[],"usage":usage}),
            )?;
        }
        job.socket.write_all(b"data: [DONE]\n\n")?;
        Ok(())
    } else {
        let mut message = json!({"role":"assistant","content":output.content,"reasoning_content":output.thoughts});
        if !output.calls.is_empty() {
            message["tool_calls"] = Value::Array(output.calls);
        }
        reply(
            &mut job.socket,
            200,
            &json!({"id":id,"object":"chat.completion","created":created,"model":alias,"choices":[{"index":0,"message":message,"finish_reason":finish}],"usage":usage}),
        )
    }
}
fn event(socket: &mut TcpStream, data: &Value) -> Result<()> {
    socket.write_all(format!("data: {}\n\n", serde_json::to_string(data)?).as_bytes())?;
    Ok(())
}
fn reply(socket: &mut TcpStream, status: u16, body: &Value) -> Result<()> {
    let bytes = serde_json::to_vec(body)?;
    let reason = match status {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        503 => "Service Unavailable",
        501 => "Not Implemented",
        _ => "Error",
    };
    write!(
        socket,
        "HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n",
        bytes.len()
    )?;
    socket.write_all(&bytes)?;
    Ok(())
}
fn read_request(socket: &mut impl Read) -> Result<Request> {
    let mut bytes = Vec::new();
    let mut buffer = [0u8; 1024];
    let header_end = loop {
        let count = socket.read(&mut buffer)?;
        if count == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "Connection closed before HTTP headers",
            )
            .into());
        }
        bytes.extend_from_slice(&buffer[..count]);
        if let Some(end) = bytes.windows(4).position(|b| b == b"\r\n\r\n") {
            if end > MAX_HEADER {
                return Err("HTTP headers too large".into());
            }
            break end + 4;
        }
        if bytes.len() > MAX_HEADER {
            return Err("HTTP headers too large".into());
        }
    };
    let header = std::str::from_utf8(&bytes[..header_end])?;
    let mut lines = header.split("\r\n");
    let first: Vec<_> = lines
        .next()
        .ok_or("Missing request line")?
        .split_whitespace()
        .collect();
    if first.len() != 3 || !matches!(first[2], "HTTP/1.1" | "HTTP/1.0") {
        return Err("Invalid HTTP request line".into());
    }
    let method = first[0].to_owned();
    let path = first[1].split('?').next().unwrap().to_owned();
    let mut length = None;
    let mut chunked = false;
    for line in lines.filter(|s| !s.is_empty()) {
        let (name, value) = line.split_once(':').ok_or("Invalid HTTP header")?;
        if name.eq_ignore_ascii_case("transfer-encoding") {
            if chunked || !value.trim().eq_ignore_ascii_case("chunked") {
                return Err("Only a single chunked Transfer-Encoding is supported".into());
            }
            chunked = true;
        }
        if name.eq_ignore_ascii_case("content-length") {
            if length.is_some() {
                return Err("Duplicate Content-Length".into());
            }
            let count = value.trim().parse::<usize>()?;
            if count > MAX_BODY {
                return Err("Request body exceeds 16 MiB".into());
            }
            length = Some(count);
        }
    }
    if chunked && length.is_some() {
        return Err("Content-Length and Transfer-Encoding cannot be combined".into());
    }
    // Header reads may already contain part of the first chunk. Preserve those
    // bytes before reading further from the socket; chunk boundaries are unrelated
    // to TCP packet boundaries or JSON tokens.
    let mut input = Cursor::new(&bytes[header_end..]).chain(socket);
    if chunked {
        return Ok(Request {
            method,
            path,
            body: read_chunked(&mut input)?,
        });
    }
    let length = length.unwrap_or(0);
    let mut body = vec![0; length];
    input.read_exact(&mut body)?;
    Ok(Request { method, path, body })
}

/// Decode HTTP/1.1 chunked bodies, including extensions and bounded trailers.
/// The same 16 MiB limit applies to decoded chunked and Content-Length bodies.
fn read_chunked(input: &mut impl Read) -> Result<Vec<u8>> {
    let mut body = Vec::new();
    loop {
        let line = read_crlf_line(input, MAX_HEADER)?;
        let size = std::str::from_utf8(&line)?.split(';').next().unwrap();
        if size.is_empty() || !size.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err("Invalid HTTP chunk size".into());
        }
        let size = usize::from_str_radix(size, 16)?;
        if size == 0 {
            let mut trailer_bytes = 0;
            loop {
                let trailer = read_crlf_line(input, MAX_HEADER - trailer_bytes)?;
                trailer_bytes += trailer.len() + 2;
                if trailer_bytes > MAX_HEADER {
                    return Err("HTTP trailers too large".into());
                }
                if trailer.is_empty() {
                    return Ok(body);
                }
                let (name, _) = std::str::from_utf8(&trailer)?
                    .split_once(':')
                    .ok_or("Invalid HTTP trailer")?;
                if name.eq_ignore_ascii_case("content-length")
                    || name.eq_ignore_ascii_case("transfer-encoding")
                {
                    return Err("Framing fields are not allowed in HTTP trailers".into());
                }
            }
        }
        let end = body
            .len()
            .checked_add(size)
            .filter(|&n| n <= MAX_BODY)
            .ok_or("Request body exceeds 16 MiB")?;
        let start = body.len();
        body.resize(end, 0);
        input.read_exact(&mut body[start..])?;
        let mut terminator = [0; 2];
        input.read_exact(&mut terminator)?;
        if terminator != *b"\r\n" {
            return Err("Missing HTTP chunk terminator".into());
        }
    }
}
fn read_crlf_line(input: &mut impl Read, limit: usize) -> Result<Vec<u8>> {
    let mut line = Vec::new();
    while line.len() < limit {
        let mut byte = [0];
        input.read_exact(&mut byte)?;
        line.push(byte[0]);
        if line.ends_with(b"\r\n") {
            line.truncate(line.len() - 2);
            return Ok(line);
        }
    }
    Err("HTTP chunk header or trailers too large".into())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore = "Requires a built .NET HttpClientProbe and local sockets; no GPU/model used"]
    fn gui_json_content_roundtrip() {
        let probe = std::env::var("LOCAL_ROCM_HTTP_PROBE").unwrap_or_else(|_| {
            format!(
                "{}/tests/HttpClientProbe/bin/Debug/net8.0/HttpClientProbe.dll",
                env!("CARGO_MANIFEST_DIR")
            )
        });
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let url = format!(
            "http://{}/v1/chat/completions",
            listener.local_addr().unwrap()
        );
        let (sender, receiver) = mpsc::sync_channel(1);
        let handler = std::thread::spawn(move || {
            let (socket, _) = listener.accept().unwrap();
            handle(socket, &sender, &json!({"model_alias":"fixture"})).unwrap();
        });
        let child = std::process::Command::new("dotnet")
            .args([&probe, &url])
            .stdout(std::process::Stdio::piped())
            .stderr(std::process::Stdio::piped())
            .spawn()
            .unwrap();
        if let Ok(mut job) = receiver.recv_timeout(Duration::from_secs(10)) {
            assert_eq!(
                job.request["probe_padding"].as_str().unwrap().len(),
                256 * 1024
            );
            job.socket.write_all(b"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nConnection: close\r\n\r\ndata: {\"choices\":[{\"delta\":{\"content\":\"4\"}}]}\n\ndata: [DONE]\n\n").unwrap();
        }
        let output = child.wait_with_output().unwrap();
        handler.join().unwrap();
        assert!(
            output.status.success(),
            "{}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
    }
    #[test]
    fn reads_body_without_consuming_next_request() {
        let mut data = &b"POST /v1/chat/completions HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}"[..];
        let r = read_request(&mut data).unwrap();
        assert_eq!(r.body, b"{}");
        assert_eq!(r.path, "/v1/chat/completions");
    }
    #[test]
    fn rejects_ambiguous_and_oversized_framing() {
        for header in [
            "Content-Length: 2\r\nContent-Length: 2",
            "Transfer-Encoding: chunked\r\nContent-Length: 2",
            "Transfer-Encoding: gzip, chunked",
            "Transfer-Encoding: chunked\r\nTransfer-Encoding: chunked",
            "Content-Length: 999999999",
        ] {
            let input = format!("POST / HTTP/1.1\r\n{header}\r\n\r\n{{}}");
            assert!(read_request(&mut input.as_bytes()).is_err());
        }
    }
    #[test]
    fn chunked_json_handles_fragmented_tcp_utf8_extensions_and_trailers() {
        struct Fragmented<'a>(&'a [u8]);
        impl Read for Fragmented<'_> {
            fn read(&mut self, output: &mut [u8]) -> io::Result<usize> {
                let count = output.len().min(self.0.len()).min(1);
                output[..count].copy_from_slice(&self.0[..count]);
                self.0 = &self.0[count..];
                Ok(count)
            }
        }
        let json = "{\"content\":\"こんにちは\"}".as_bytes();
        let mut bytes =
            b"POST /v1/chat/completions HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n".to_vec();
        for chunk in json.chunks(2) {
            bytes.extend(format!("{:X};extension=yes\r\n", chunk.len()).as_bytes());
            bytes.extend(chunk);
            bytes.extend(b"\r\n");
        }
        bytes.extend(b"0\r\nX-Checksum: test\r\n\r\n");
        assert_eq!(read_request(&mut Fragmented(&bytes)).unwrap().body, json);
        assert_eq!(read_request(&mut bytes.as_slice()).unwrap().body, json);
    }
    #[test]
    fn rejects_incomplete_oversized_or_malformed_chunks() {
        for body in [
            "g\r\n",
            "+1\r\na\r\n0\r\n\r\n",
            "1\r\na!!",
            "3\r\nab",
            "1000001\r\n",
            "0\r\nContent-Length: 1\r\n\r\n",
        ] {
            let data = format!("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n{body}");
            assert!(read_request(&mut data.as_bytes()).is_err(), "{body:?}");
        }
    }
    #[test]
    fn peer_cancellation_is_not_a_server_failure() {
        for kind in [
            io::ErrorKind::ConnectionAborted,
            io::ErrorKind::ConnectionReset,
            io::ErrorKind::BrokenPipe,
            io::ErrorKind::UnexpectedEof,
        ] {
            assert!(is_disconnect(&io::Error::from(kind)));
        }
        assert!(!is_disconnect(&io::Error::from(
            io::ErrorKind::PermissionDenied
        )));
    }
}

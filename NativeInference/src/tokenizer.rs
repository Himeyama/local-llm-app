//! Byte-level BPE reconstructed from the GGUF vocabulary and merge table.
use crate::{Result, gguf::Gguf};
use regex::Regex;
use std::collections::HashMap;

const QWEN35_SPLIT: &str = r"'(?i:[sdmt]|ll|ve|re)|[^\r\n\p{L}\p{N}]?[\p{L}\p{M}]+|\p{N}| ?[^\s\p{L}\p{M}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+";

pub struct Tokenizer {
    vocabulary: HashMap<String, u32>,
    tokens: Vec<String>,
    merges: HashMap<(String, String), usize>,
    byte_chars: Vec<char>,
    char_bytes: HashMap<char, u8>,
    special: Vec<(String, u32)>,
    split: Regex,
    pub eos: u32,
}
impl Tokenizer {
    pub fn from_gguf(model: &Gguf) -> Result<Self> {
        if model.string("tokenizer.ggml.model")? != "gpt2"
            || model.string("tokenizer.ggml.pre")? != "qwen35"
        {
            return Err("Only the Qwen35 byte-BPE tokenizer is supported".into());
        }
        let strings = |key: &str| -> Result<Vec<String>> {
            model
                .metadata
                .get(key)
                .and_then(|v| v.as_array())
                .ok_or_else(|| format!("Missing {key}"))?
                .iter()
                .map(|v| {
                    v.as_str()
                        .map(str::to_owned)
                        .ok_or_else(|| "Non-string tokenizer entry".into())
                })
                .collect()
        };
        let tokens = strings("tokenizer.ggml.tokens")?;
        let vocabulary: HashMap<_, _> = tokens
            .iter()
            .enumerate()
            .map(|(i, t)| (t.clone(), i as u32))
            .collect();
        if vocabulary.len() != tokens.len() {
            return Err("Duplicate vocabulary entries".into());
        }
        let merges = strings("tokenizer.ggml.merges")?
            .into_iter()
            .enumerate()
            .map(|(rank, pair)| {
                let (a, b) = pair.split_once(' ').ok_or("Invalid BPE merge pair")?;
                Ok(((a.to_owned(), b.to_owned()), rank))
            })
            .collect::<Result<HashMap<_, _>>>()?;
        let types = model
            .metadata
            .get("tokenizer.ggml.token_type")
            .and_then(|v| v.as_array())
            .ok_or("Missing token types")?;
        if types.len() != tokens.len() {
            return Err("Vocabulary/type count mismatch".into());
        }
        let mut special: Vec<_> = tokens
            .iter()
            .enumerate()
            .filter(|(i, _)| matches!(types[*i].as_u64(), Some(3 | 4)))
            .map(|(i, t)| (t.clone(), i as u32))
            .collect();
        special.sort_by_key(|(t, _)| std::cmp::Reverse(t.len()));
        let byte_chars = byte_alphabet();
        let char_bytes = byte_chars
            .iter()
            .enumerate()
            .map(|(i, &c)| (c, i as u8))
            .collect();
        // Rust's linear-time regex engine has no lookahead. The whitespace arm
        // is adjusted below to reproduce the final-space lookahead in Qwen BPE.
        let split = Regex::new(QWEN35_SPLIT)?;
        let eos = u32::try_from(model.integer("tokenizer.ggml.eos_token_id")?)?;
        if eos as usize >= tokens.len() {
            return Err("EOS ID exceeds vocabulary".into());
        }
        Ok(Self {
            vocabulary,
            tokens,
            merges,
            byte_chars,
            char_bytes,
            special,
            split,
            eos,
        })
    }
    pub fn vocabulary_size(&self) -> usize {
        self.tokens.len()
    }
    pub fn encode(&self, text: &str) -> Result<Vec<u32>> {
        let mut ids = Vec::new();
        let mut remaining = text;
        while !remaining.is_empty() {
            let next = self
                .special
                .iter()
                .filter_map(|(token, id)| remaining.find(token).map(|offset| (offset, token, *id)))
                .min_by_key(|(offset, token, _)| (*offset, std::cmp::Reverse(token.len())));
            if let Some((offset, token, id)) = next {
                self.ordinary(&remaining[..offset], &mut ids)?;
                ids.push(id);
                remaining = &remaining[offset + token.len()..];
            } else {
                self.ordinary(remaining, &mut ids)?;
                break;
            }
        }
        Ok(ids)
    }
    fn ordinary(&self, text: &str, ids: &mut Vec<u32>) -> Result<()> {
        let mut offset = 0;
        while offset < text.len() {
            let found = self
                .split
                .find_at(text, offset)
                .ok_or("Tokenizer failed to cover input")?;
            if found.start() != offset {
                return Err("Tokenizer skipped input bytes".into());
            }
            let mut end = found.end();
            let piece = &text[offset..end];
            if end < text.len()
                && piece.chars().all(char::is_whitespace)
                && !piece.contains(['\r', '\n'])
                && piece.chars().count() > 1
            {
                end -= piece.chars().last().unwrap().len_utf8();
            }
            let mut parts: Vec<String> = text.as_bytes()[offset..end]
                .iter()
                .map(|&b| self.byte_chars[b as usize].to_string())
                .collect();
            while parts.len() > 1 {
                let best = parts
                    .windows(2)
                    .enumerate()
                    .filter_map(|(i, p)| {
                        self.merges
                            .get(&(p[0].clone(), p[1].clone()))
                            .map(|&rank| (rank, i))
                    })
                    .min();
                let Some((_, i)) = best else { break };
                let right = parts.remove(i + 1);
                parts[i].push_str(&right);
            }
            for part in parts {
                ids.push(
                    *self
                        .vocabulary
                        .get(&part)
                        .ok_or("Merged token is absent from vocabulary")?,
                );
            }
            offset = end;
        }
        Ok(())
    }
    pub fn decode_bytes(&self, ids: &[u32]) -> Result<Vec<u8>> {
        let mut result = Vec::new();
        for &id in ids {
            let token = self
                .tokens
                .get(id as usize)
                .ok_or("Token ID exceeds vocabulary")?;
            for c in token.chars() {
                result.push(
                    *self
                        .char_bytes
                        .get(&c)
                        .ok_or("Token contains a non-byte character")?,
                );
            }
        }
        Ok(result)
    }
    pub fn token_text(&self, id: u32) -> Result<&str> {
        self.tokens
            .get(id as usize)
            .map(String::as_str)
            .ok_or_else(|| "Invalid token ID".into())
    }
}
fn byte_alphabet() -> Vec<char> {
    let visible =
        |b: u32| (33..=126).contains(&b) || (161..=172).contains(&b) || (174..=255).contains(&b);
    let mut extra = 256;
    (0..256)
        .map(|b| {
            let code = if visible(b) {
                b
            } else {
                let c = extra;
                extra += 1;
                c
            };
            char::from_u32(code).unwrap()
        })
        .collect()
}
#[cfg(test)]
mod tests {
    #[test]
    fn combining_marks_stay_with_their_base_letters() {
        let split = regex::Regex::new(super::QWEN35_SPLIT).unwrap();
        let parts: Vec<_> = split
            .find_iter("a\u{301} か\u{3099} 123")
            .map(|m| m.as_str())
            .collect();
        assert_eq!(parts, vec!["a\u{301}", " か\u{3099}", " ", "1", "2", "3"]);
    }
    #[test]
    fn byte_alphabet_is_reversible() {
        let a = super::byte_alphabet();
        assert_eq!(a.len(), 256);
        assert_eq!(
            a.iter().collect::<std::collections::HashSet<_>>().len(),
            256
        );
        assert_eq!(a[32], 'Ġ');
        assert_eq!(a[10], 'Ċ');
    }
}

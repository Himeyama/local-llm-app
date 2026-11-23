//! Real BPE regression: byte prefixes need not be token prefixes when whitespace
//! at the generation boundary is merged with the next assistant message.
use local_rocm::{
    Result,
    chat::Prompt,
    gguf::Gguf,
    prompt_cache::{Reuse, choose},
    tokenizer::Tokenizer,
};
use serde_json::json;

#[test]
#[ignore = "Requires the local GGUF vocabulary; no GPU or server is loaded"]
fn continued_conversation_reuses_checkpoint_after_reasoning_is_removed() -> Result<()> {
    let path = std::env::var_os("LOCAL_ROCM_TEST_MODEL")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| {
            std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("../models/Qwen3.8-27B-Uncensored/Qwen3.8-27B-Uncensored-Q4_K_M.gguf")
        });
    let tokenizer = Tokenizer::from_gguf(&Gguf::open(path)?)?;
    let first = Prompt::from_request(&json!({"messages":[
        {"role":"system","content":"Answer in Japanese."},
        {"role":"user","content":"こんにちは"}
    ],"reasoning_effort":"xhigh"}))?;
    let prefix = tokenizer.encode(&first.text[..first.cache_boundary])?;
    let first_ids = tokenizer.encode(&first.text)?;
    let checkpoint = &prefix[..prefix
        .iter()
        .zip(&first_ids)
        .take_while(|(a, b)| a == b)
        .count()];
    for content in [
        "\n\nこんにちは。",
        "\n\n```rust\nfn main() {}\n```",
        "回答です。",
    ] {
        let next = Prompt::from_request(&json!({"messages":[
            {"role":"system","content":"Answer in Japanese."},
            {"role":"user","content":"こんにちは"},
            {"role":"assistant","content":content},
            {"role":"user","content":"続きをお願いします"}
        ],"reasoning_effort":"xhigh"}))?;
        let next_ids = tokenizer.encode(&next.text)?;
        assert_eq!(
            choose(&next_ids, &first_ids, true, false, Some(checkpoint)),
            Reuse::Checkpoint(checkpoint.len()),
            "assistant content {content:?}; checkpoint length {}",
            checkpoint.len()
        );
    }
    Ok(())
}

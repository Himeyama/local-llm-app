//! GGUF storage and ROCm execution, independent of llama.cpp and ggml.
#[cfg(windows)]
pub mod benchmarks;
pub mod chat;
pub mod gguf;
#[cfg(windows)]
pub mod model;
pub mod prompt_cache;
pub mod quant;
#[cfg(windows)]
pub mod rocm;
#[cfg(windows)]
pub mod server;
pub mod tokenizer;

pub type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

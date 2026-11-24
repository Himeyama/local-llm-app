# Prompt prefix simulator

Run `python tools/cache_simulator.py` for a synthetic five-request file-read
scenario, or `python tools/cache_simulator.py requests.jsonl` to analyze a
sequence of recorded OpenAI-style requests. Each JSONL line must contain
`model`, `messages`, and optionally `tools`, in request order. Input files can
contain sensitive prompts and tool results; keep them private.

The simulator compares each request against the previous request using one
unlimited, always-warm cache slot. It serializes the model, tools and messages
into a deterministic proxy prompt and estimates tokens using the same
character-based heuristic as `ChatCompaction.EstimateText`. It does not use a
model's chat template or tokenizer, account for generated tokens, TTL, slot
eviction, batching, compaction calls, images or provider cache rules. It is a
prefix-reuse estimate, not a measurement of actual llama-server cache hits.
The built-in scenarios assume the needed file content is on the first page and
the same number of requests are made; they are not agent-quality benchmarks.

## Live API experiment

With `OPENAI_API_KEY` set, run `python tools/cache_live_test.py --model gpt-6-luna`.
This sends four paid OpenAI API requests containing generated text, not workspace
files. Each arm uses a different random prefix, followed by two conversation
turns. The tool prints the API's input, cached and output token counts and elapsed
times. A missing cache count is reported as null, not zero. This isolates prompt
size and reuse; it does not run the GUI, exercise tool calls, establish answer
quality, or measure llama-server. API model availability and pricing vary.

`python tools/cache_full_read_test.py` compares a 48,000-character synthetic
file delivered whole with the same content delivered sequentially in four
12,000-character pages. Each delivery gets an API response and both cases end
with one additional request. It uses simulated user messages, not actual GUI
tool calls, and reports per-request API usage and elapsed times. It issues seven
potentially billable requests and does not evaluate task quality.

## Duplicate read experiment

`python tools/duplicate_read_benchmark.py` sends 12 paid requests with synthetic
300-line files. It compares repeated full `read_file` observations with a
prototype that replaces an exact duplicate with a reference to the earlier full
result. It checks exact answers for two unchanged files and one changed file.
The transcript is constructed by the benchmark; it is not a full coding-agent
run. `duplicate_read_prototype.py` is experimental and is not used by the GUI.
Run its checks with `python -m unittest discover -s tests -p duplicate_read_prototype_test.py`.

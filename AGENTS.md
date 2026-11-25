# Native inference server deployment

When changing the native inference server, build the release binary and copy the updated `local-rocm.exe` to both locations:

- `runtime/native/local-rocm.exe` in this repository.
- `C:\Users\hikari\AppData\Local\Programs\LocalLlmGui\app\runtime\native\local-rocm.exe`.

The installed location is outside the workspace and may require sandbox escalation. Verify both copies match the release binary and tell the user to restart the server.

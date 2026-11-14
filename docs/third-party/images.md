# Image generation services: reference for a C# workflow app

Researched 2026-11-14 from the official docs, plus the `openai/codex` source at commit `b1573f1` (2026-11-14). No account was created and no API key was used, so nothing here has been tested against a live endpoint.

Each item is tagged with how well it was confirmed:
- **[doc]**: read on an official docs page.
- **[src]**: read in the official source code.
- **[unconfirmed]**: inferred, from a third party, or missing from the docs.

In the example JSON, field names and nesting come from the docs or source. Values such as ids, timestamps and token counts are illustrative only.

---

## 0. Summary

| Service | Credential | Base URL | Main call | Sync/async | Image bytes location | Free key check |
|---|---|---|---|---|---|---|
| OpenAI Images API | API key, sent as `Authorization: Bearer` | `https://api.openai.com/v1` | `POST /images/generations`, `POST /images/edits` | Sync; SSE if `stream:true` | `data[i].b64_json` (base64) | `GET /v1/models` |
| OpenAI Responses tool | Same | Same | `POST /responses` with `tools:[{type:"image_generation"}]` | Sync; SSE if `stream:true`; `background` possible | `output[i].result` where `output[i].type=="image_generation_call"` | Same |
| ChatGPT subscription | ChatGPT sign-in through the Codex CLI only | n/a (Codex drives an internal backend) | `codex app-server` JSON-RPC or `codex exec` | Long-running turn | File at `$CODEX_HOME/generated_images/<threadId>/<callId>.png`, plus base64 or `fileId` in the `imageGeneration` item | `account/read` in app-server, or `codex login status` [unconfirmed] |
| Google Gemini (Nano Banana) | AI Studio API key, sent as `x-goog-api-key` | `https://generativelanguage.googleapis.com` | `POST /v1beta/interactions` (recommended) or `POST /v1{beta}/models/{model}:generateContent` (legacy, still supported) | Sync; `background:true` plus `GET /v1beta/interactions/{id}` polling; streaming | Interactions: `steps[].content[]` with `type:"image"` and `data`. generateContent: `candidates[0].content.parts[].inlineData.data` | `GET /v1beta/models` |
| Imagen | Shut down | n/a | n/a | n/a | n/a | n/a |
| Midjourney | **No official public API.** Automating it breaks the ToS. | n/a | n/a | n/a | n/a | n/a |

---

## 1. OpenAI

### 1a. API key route

#### Auth and headers [doc]
- Use `Authorization: Bearer <OPENAI_API_KEY>` (HTTP Bearer authentication).
- Optional headers for accounts in several organizations or using legacy user keys: `OpenAI-Organization: <org id>` and `OpenAI-Project: <project id>`. Usage is billed to whichever org and project you name.
- Optional `X-Client-Request-Id: <ascii ≤512 chars>`. A longer or non-ASCII value causes a 400 error.
- Response headers:
  - `x-request-id`: log this for support requests.
  - `openai-organization`, `openai-processing-ms`, `openai-version`.
  - `x-ratelimit-limit-requests|tokens`, `x-ratelimit-remaining-requests|tokens`, `x-ratelimit-reset-requests|tokens`.
  - `x-ratelimit-*-project-tokens`, sometimes.
- **Key format [unconfirmed]:** the API reference does not document a prefix. Keys are usually `sk-proj-…` (project keys) or `sk-…`, so don't hard-validate the prefix. At most, warn when a key doesn't start with `sk-`.
- **Organization verification:** "Using GPT Image models may require API Organization Verification" [doc]. A key can pass the free check and still get a 403 on image models.

#### Free key check [doc]
```
GET https://api.openai.com/v1/models
Authorization: Bearer sk-...
```
A 200 response looks like this:
```json
{
  "object": "list",
  "data": [
    {"id": "gpt-image-2.5-flare", "object": "model", "created": 1757289600, "owned_by": "openai", "shutdown_date": null}
  ]
}
```
- `shutdown_date` is optional (string or null), for example `"2026-10-23"`.
- A bad key returns 401.
- To check that image models are visible, look for `id` values starting with `gpt-image-`. Whether a listed model is also usable (organization verification) is [unconfirmed]; only a real generation call proves it.

#### Current image models [doc]
These values are accepted by `model` on `/v1/images/generations`:
- `gpt-image-2.5-sunburst` (alias) and `gpt-image-2.5-sunburst-2026-09-08`: "where editing precision matters most". This is the **default model for `/images/edits`**.
- `gpt-image-2.5-flare` (alias) and `gpt-image-2.5-flare-2026-09-08`: fast, high-quality everyday generation.
- `gpt-image-2.6-sunburst` and `gpt-image-2.6-flare`:
  - Add `complexity` control, `markup`-guided edits, SVG output and animated GIF output.
  - The Responses tool reference lists `gpt-image-2.6-sunburst` as the tool's default model.
- Older models:
  - `gpt-image-2` and `gpt-image-2-2026-04-21`
  - `gpt-image-1.5`
  - `gpt-image-1-mini`
  - `chatgpt-image-latest`
  - `dall-e-2` and `dall-e-3` (legacy; these support `response_format`/`style`)
- **`gpt-image-1` was shut down on 2026-10-23.**
- The docs say to always set `model` explicitly.

#### POST /v1/images/generations (JSON) [doc]
Request fields:

| Field | Values | Default |
|---|---|---|
| `model` | see the list above | set it explicitly |
| `prompt` | string, ≤ 32,000 chars | required |
| `n` | 1–10 | 1 |
| `size` | `auto`, `1024x1024`, `1536x1024`, `1024x1536`. gpt-image-2+ also accept custom `WIDTHxHEIGHT` (rules below). DALL·E only: `256x256`, `512x512`, `1792x1024`, `1024x1792`. | `auto` |
| `quality` | `low`, `medium`, `high`, `auto`. `xhigh` and `max` work only on the 2.5 sunburst/flare models. DALL·E: `standard`, `hd`. | `auto` |
| `background` | `transparent`, `opaque`, `auto`. Transparent needs `png` or `webp`. | `auto` |
| `output_format` | `png`, `webp`, `jpeg`, `gif`, `svg` | `png` |
| `output_compression` | 0–100, for jpeg/webp only | 100 |
| `moderation` | `auto`, `low` | `auto` |
| `stream` | bool | false |
| `partial_images` | 0–3, streaming only. Each partial image adds 100 output tokens of cost. | 0 |
| `complexity` | `low`, `medium`, `high` (2.6). No extra fee. | `high` |
| `frame_count` | 2–16, GIF only | 9 |
| `frame_duration_ms` | 20–2000, rounded to 10 ms, GIF only | 200 |
| `svg` | `{ "model": "...", "reasoning": {"effort": "..."} }`, SVG only | n/a |
| `safety_identifier` | string ≤128, a hashed stable end-user id. Replaces the deprecated `user`. | n/a |
| `response_format` (`url`/`b64_json`), `style` | DALL·E only. **GPT image models always return `b64_json`.** | n/a |

Custom size rules for gpt-image-2 and the 2.5 models:
- Both edges must be multiples of 16.
- Aspect ratio must be between 1:3 and 3:1.
- No edge may exceed 3840 px.
- Total pixels must be between 655,360 and 8,294,400. The edits reference also states a maximum of 3840x2160.
- Sizes above 2560x1440 are experimental.

SVG constraints:
- Requires `n=1`.
- No streaming, partial images or compression.
- Generations only (not edits).
- Decode `b64_json`, which holds UTF-8 XML, and save it as `.svg`.

GIF constraints:
- No streaming, partial images, masks or custom compression.

Example request:
```json
{
  "model": "gpt-image-2.5-flare",
  "prompt": "A cute baby sea otter",
  "size": "1024x1024",
  "quality": "medium",
  "background": "transparent",
  "output_format": "png"
}
```

Response (`ImagesResponse`):
```json
{
  "created": 1713833628,
  "background": "transparent",
  "output_format": "png",
  "quality": "medium",
  "size": "1024x1024",
  "data": [ { "b64_json": "iVBORw0KGgo..." } ],
  "usage": {
    "input_tokens": 50, "output_tokens": 1056, "total_tokens": 1106,
    "input_tokens_details": { "text_tokens": 50, "image_tokens": 0 },
    "output_tokens_details": { "text_tokens": 0, "image_tokens": 1056 }
  }
}
```
- GPT image models return neither `data[].revised_prompt` nor `data[].url`.
- `svg_usage` (`input_tokens`, `model`, `output_tokens`, `reasoning_tokens`, `total_tokens`) appears for SVG output.
- `safety_identifier` is echoed back when sent.

#### POST /v1/images/edits [doc]
The docs show two request encodings:

**Multipart** (`multipart/form-data`):
```
-F "model=gpt-image-2.5-sunburst"
-F "image[]=@a.png" -F "image[]=@b.png"
-F "mask=@mask.png"
-F "prompt=..."
```

**JSON** (`application/json`):
```json
{
  "model": "gpt-image-2.5-sunburst",
  "prompt": "Create a lovely gift basket with these items in it",
  "images": [
    { "image_url": "https://example.com/source-image.png" },
    { "image_url": "data:image/png;base64,iVBOR..." },
    { "file_id": "file-abc123" }
  ],
  "mask": { "image_url": "data:image/png;base64,..." },
  "size": "1024x1024",
  "quality": "high"
}
```

Field rules:
- `images` takes 1–16 items, each with exactly one of `image_url` (URL or base64 data URL, ≤ 20,971,520 chars) or `file_id`.
- `mask` takes exactly one of `image_url` or `file_id`.
  - It needs an alpha channel and must match the first image's format and size, under 50 MB.
  - It applies to the **first** image.
  - Masking is prompt-guided, so the model may not follow the mask's exact shape.
- `markup` is a transparent PNG annotation layer on the first image, for 2.5/2.6 models.
  - It must match that image's dimensions.
  - It can't be combined with `mask`.
  - Limits are ≤ 16,777,216 px and ≤ 50 MB.
- `input_fidelity` (`high` or `low`):
  - `gpt-image-1.5` accepts both.
  - `gpt-image-1-mini` accepts only `low`.
  - **Omit it for gpt-image-2 and newer**, which always use high fidelity.
- Everything else (`n`, `size`, `quality`, `background`, `output_format` including gif but not svg, `output_compression`, `moderation`, `stream`, `partial_images`, `safety_identifier`) works as on generations.
- The response is the same `ImagesResponse` shape.

#### Streaming the Images API (SSE) [doc]
Set `"stream": true, "partial_images": 1..3`. The server sends Server-Sent Events:
```
event: image_generation.partial_image
data: {"type":"image_generation.partial_image","b64_json":"...","partial_image_index":0}

event: image_generation.completed
data: {"type":"image_generation.completed","b64_json":"...","usage":{"total_tokens":..,"input_tokens":..,"output_tokens":..,"input_tokens_details":{"text_tokens":..,"image_tokens":..}}}
```
- The edits endpoint uses `image_edit.partial_image` and `image_edit.completed`, with the same fields.
- Fewer partial images may arrive than requested if the final image finishes first.

#### Image generation as a tool in the Responses API [doc]
```
POST https://api.openai.com/v1/responses
```
```json
{
  "model": "gpt-6-astra",
  "input": [
    { "role": "user", "content": [
      { "type": "input_text", "text": "Make this cat wear a hat" },
      { "type": "input_image", "image_url": "data:image/png;base64,..." }
    ]}
  ],
  "tools": [{
    "type": "image_generation",
    "model": "gpt-image-2.5-sunburst",
    "action": "auto",
    "size": "1024x1024",
    "quality": "high",
    "background": "auto",
    "output_format": "png",
    "output_compression": 100,
    "moderation": "auto",
    "partial_images": 0
  }],
  "tool_choice": { "type": "image_generation" }
}
```
- The top-level `model` must be a mainline model that supports the tool. The docs list `chat-latest`, `gpt-5.2`, `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.5`, `gpt-5.6-*`, `gpt-6-astra`, `gpt-6-luna`, `gpt-6-sol`, `gpt-6.1-sol`, `gpt-6.1-terra` and others, noting that "gpt-5 and newer should support" it.
- Set `tool_choice: {"type":"image_generation"}` to force the tool.

Tool fields:
- `action`: `auto`, `generate`, or `edit`. `edit` with no image in context returns an error.
- `input_image_mask`: `{file_id | image_url}`.
- `input_fidelity`: as on the Images API.
- `complexity`, `frame_count`, `frame_duration_ms`, `svg`: as on the Images API.
- `quality` adds `xhigh` and `max` for the 2.5 models.

`input_image` content part fields:
- `image_url` (URL or data URL)
- `file_id`
- `detail`: `low`, `high`, `auto` or `original`

Output: an item in `response.output[]`:
```json
{ "id": "ig_123", "type": "image_generation_call", "status": "completed",
  "revised_prompt": "A gray tabby cat hugging an otter...", "result": "<base64 image>" }
```

Multi-turn editing has two options:
- Pass `previous_response_id`.
- Put `{ "type": "image_generation_call", "id": "ig_123" }` (Python example) or `{ "type": "item_reference", "id": "ig_123" }` (TS example) into `input`.

Streaming (`stream: true`) sends these SSE events:
- `response.image_generation_call.in_progress`, with `{item_id, output_index, sequence_number}`.
- `response.image_generation_call.generating`, with the same fields.
- `response.image_generation_call.partial_image`, with `{item_id, output_index, partial_image_b64, partial_image_index (0-based), sequence_number}`.
- `response.image_generation_call.completed`, with `{item_id, output_index, sequence_number}`. The final base64 is **not** in this event. Read it from `response.completed` → `response.output[]` → `image_generation_call.result`, or from `response.output_item.done`.

Prompt-cache pricing for image inputs applies only to this tool, not to `/v1/images/*`.

Consistency note:
- The `gpt-image-2.5-flare` model page marks `v1/responses` as "Not supported", but the guide says the model can be chosen as the tool's `model`.
- The tool's default model is `gpt-image-2.6-sunburst`, while `/images/edits` defaults to `gpt-image-2.5-sunburst`.
- Always set `model` explicitly.

#### Errors and moderation [doc]
- A blocked request returns `error.type = "image_generation_user_error"` and `error.code = "moderation_blocked"`.
- It may also include `moderation_details: { moderation_stage: "input"|"output"|"unknown", categories: [...] }`.
- Don't auto-retry user or moderation errors. Retry 429 and 5xx with backoff. Don't retry quota errors.
- Latency: "Complex prompts may take up to 2 minutes", so give the HttpClient a timeout of at least 180 s.

#### Pricing [doc]
GPT-Image-2.5 (sunburst and flare), per 1M tokens:
- Image input $8 (cached $2)
- Text input $5 (cached $1.25)
- Image output $30 (Batch $15)

GPT-Image-2.6: image output $30 per 1M tokens.

Per-image examples at 1024²:
- gpt-image-2: low $0.006, medium $0.053, high $0.211.
- gpt-image-1-mini: low $0.005, medium $0.011, high $0.036.

#### Rate limits [doc]
For `gpt-image-2.5-flare`:

| Tier | TPM | IPM |
|---|---|---|
| Build | 250,000 | 20 |
| Launch | 3,000,000 | 150 |
| Grow | 8,000,000 | 250 |

No RPM figure is listed. Read the `x-ratelimit-*` headers at runtime.

#### Docs used
- https://developers.openai.com/api/docs/guides/image-generation (formerly platform.openai.com/docs/guides/image-generation)
- https://developers.openai.com/api/reference/resources/images/methods/generate
- https://developers.openai.com/api/reference/resources/images/methods/edit
- https://developers.openai.com/api/docs/guides/tools-image-generation
- https://developers.openai.com/api/reference/resources/responses/methods/create
- https://developers.openai.com/api/reference/resources/responses/streaming-events
- https://developers.openai.com/api/reference/resources/models/methods/list
- https://developers.openai.com/api/reference/overview
- https://developers.openai.com/api/docs/models/gpt-image-2.5-flare

---

### 1b. ChatGPT subscription instead of an API key

**Bottom line:** OpenAI has no public HTTP API that takes ChatGPT-subscription credentials. The only official, programmatic route is the **Codex CLI** signed in with ChatGPT. Codex has a built-in image tool that runs on the plan's Codex usage limits.

Do not call the internal backend Codex uses with a copied ChatGPT token. That backend is `https://chatgpt.com/backend-api/codex/images/generations|edits` [src: `model-provider-info/src/lib.rs` `CHATGPT_CODEX_BASE_URL`, `codex-api/src/endpoint/images.rs`]. It is undocumented, private, and not a supported interface. Drive the `codex` binary instead.

#### What the official Codex docs say [doc]
Source: https://learn.chatgpt.com/docs/image-generation (redirected from developers.openai.com/codex).
- Image generation works in the Codex app, the CLI and the IDE extension.
- It uses **`gpt-image-2`**.
- Generations "count toward general Codex usage limits". They use included limits "3–5x faster on average than similar turns without image generation", depending on quality and size.
- Setting `OPENAI_API_KEY` switches generation to API pricing; the docs suggest this for large batches.
- In the CLI, ask in natural language or include `$imagegen` in the prompt. Attach reference images with `-i/--image`.
- **Which plans include it is not stated on the official page [unconfirmed].** Third-party write-ups say Plus and Pro do (Pro with higher limits) and Free does not.

#### What the source code shows [src]
The code lives in `codex-rs/ext/image-generation/`.

**Feature flags** (`codex-rs/features/src/lib.rs`):
- `image_generation`: stage **Stable**, **default on**. The legacy alias `imagegenext` is still accepted.
- `image_generation_animation`: stage UnderDevelopment, default off. Enables GIF output (released in v0.168.0, 2026-11-13).

You can toggle them in three ways. `--enable`/`--disable` are global CLI flags folded into config overrides.
```toml
# ~/.codex/config.toml   (on Windows: %USERPROFILE%\.codex\config.toml, or $CODEX_HOME)
[features]
image_generation = true
image_generation_animation = true   # optional, GIFs
```
```
codex --disable image_generation ...
codex -c features.image_generation=false ...
```

**Availability condition** (`extension.rs`):
- The tool is offered only when the model provider is OpenAI (or requires OpenAI auth).
- With ChatGPT sign-in, requests go to the ChatGPT Codex backend. With an API key, they go to `https://api.openai.com/v1/images/...`.

**The tool the model sees:**
- Namespace `image_gen`, function `imagegen`, exposed directly.
- The model decides when to call it, so tell it to generate an image or use `$imagegen`.
- Arguments (`ImagegenArgs`, `deny_unknown_fields`):
  ```json
  { "prompt": "string",
    "transparent_background": false,
    "referenced_image_paths": ["C:\\abs\\path.png"],     // ≤5, absolute paths, for edits
    "num_last_images_to_include": 1,                      // 1..5, mutually exclusive with the above
    "gif": { "frame_count": 9, "frame_duration_ms": 200 } // only if image_generation_animation is on
  }
  ```
- The backend request is fixed: `{"prompt", "background": "transparent"|"opaque", "model": "gpt-image-2", "quality": "auto", "size": "auto"}`.
- **You cannot pick size, quality, n or format.** You get one image, PNG (or GIF), at most 5 edit inputs.

**Where the file goes** (`artifact.rs`, `app-server/src/extensions.rs`):
- `$CODEX_HOME/generated_images/<threadId>/<callId>.png` (or `.gif`). `$CODEX_HOME` defaults to `~/.codex`.
- Characters in the ids other than `[A-Za-z0-9_-]` become `_`.
- If no save root is set, the file is written to `<cwd>/generated_images/`.
- The model is told the path and is told to copy, not move, the file if it is needed elsewhere.

**App-server events** (`codex app-server`, JSON-RPC 2.0 over stdio). A ThreadItem with `"type": "imageGeneration"` arrives first in `item/started` (status `in_progress`) and then in `item/completed`:
```json
{ "method": "item/completed",
  "params": { "threadId": "...", "turnId": "...", "completedAtMs": 1763140000000,
    "item": {
      "type": "imageGeneration",
      "id": "<callId>",
      "status": "completed",                 // "in_progress" | "completed" | "failed"
      "revisedPrompt": "paint a blue whale",  // the prompt the model sent
      "result": "<base64 png>",               // OR "fileId": "<attachment id>" (untagged union)
      "transparentBackground": false,         // optional
      "failure": null,                        // or {"type":"usageLimitExceeded","limitId":"image_gen"|"imagegen_premium","resetsAt":1763150000|null}
      "savedPath": "%USERPROFILE%\.codex\\generated_images\\<thread>\\<call>.png",  // optional
      "inputImages": [ { "path": "C:\\...\\ref.png", "sha256": "<hex>" } ],         // optional
      "generationId": "gen-..."               // or null
    } } }
```
- TS schema files: `codex-rs/app-server-protocol/schema/typescript/ImageGenerationItem.ts`, `ImageGenerationFailure.ts`, `ImageGenerationInputImage.ts` and `v2/ThreadItem.ts`.
- **Prefer `savedPath`** and read the file from disk. `result` may be empty, or replaced by `fileId`, when the attachment store holds the image.
- The feature `omit_app_server_notification_media` (off by default) strips inline image data from notifications.
- A raw Responses item `image_generation_call` (`{type, status, result, ...}`) can also appear in `rawResponseItem/completed` when the model uses a native Responses tool. That is the legacy path; the extension tool above is the current one.

**`codex exec`:**
- It runs the same engine through an in-process app server, so the tool and save path are the same. That this path uses the same `$CODEX_HOME` save root is [unconfirmed by runtime test], but the code shares the installation.
- **`codex exec --json` does not emit image-generation items.** Its JSONL converter (`exec/src/event_processor_with_jsonl_output.rs`) maps only agentMessage, reasoning, commandExecution, fileChange, mcpToolCall, collabToolCall, webSearch, todoList and error, and drops the rest.
- With `exec`, find the image by scanning `$CODEX_HOME/generated_images/<threadId>/` after the run (the thread id comes from the `thread.started` JSONL event), or from the agent's final message.
- **Use `codex app-server` when you need structured image events.**

**Limits:**
- Usage counts against the ChatGPT plan's Codex limits. Hitting them yields `failure.type = "usageLimitExceeded"` with `limitId` `image_gen` or `imagegen_premium`.
- Generation takes minutes, so allow a generous timeout.
- The output file has a 32 MB limit when written to the cwd.
- Usage policy: you need consent for real people's likenesses, and should avoid imitating brands or artists.

**Release notes** (GitHub releases):
- v0.164.0 (2026-10-23): forwards turn metadata to image requests, stores generated images as attachment references, records local inputs.
- v0.166.0 (2026-10-29): image generation failure metadata for premium usage limits, exposes `generation_id`.
- v0.168.0 (2026-11-13): animated GIF via `features.image_generation_animation`.

**Bundled `imagegen` skill** (`codex-rs/skills/src/assets/samples/imagegen/SKILL.md`):
- Default mode uses the built-in `image_gen` tool ("Does not require `OPENAI_API_KEY`").
- A fallback CLI script, `scripts/image_gen.py`, does need `OPENAI_API_KEY`.

#### Docs used
- https://learn.chatgpt.com/docs/image-generation
- https://learn.chatgpt.com/docs/config-file/config-reference (the image flags are not documented there)
- https://github.com/openai/codex at `b1573f1`:
  - `codex-rs/ext/image-generation/src/{tool.rs,artifact.rs,backend.rs,extension.rs}`
  - `codex-rs/ext/image-generation/imagegen_description.md`
  - `codex-rs/features/src/lib.rs`
  - `codex-rs/app-server-protocol/schema/typescript/*`
  - `codex-rs/app-server/tests/suite/v2/imagegen_extension.rs`
  - `codex-rs/exec/src/event_processor_with_jsonl_output.rs`
- Releases: https://github.com/openai/codex/releases (rust-v0.164.0, v0.166.0, v0.168.0)

---

## 2. Google Gemini image models (Nano Banana)

### Auth [doc]
- Send the header `x-goog-api-key: <key>`. The `?key=<key>` query parameter also works, but the header is preferable because URLs end up in logs.
- Keys are created at https://aistudio.google.com/apikey and each belongs to a Google Cloud project. Billing and rate limits are per **project**, not per key.
- **Key types changed in 2026:**
  - Since 2026-05-28, new AI Studio keys are "authorization keys", bound to a service account and restricted to the Generative Language API.
  - The API **rejects unrestricted standard keys**. Standard keys that have explicit restrictions still work.
- **Key format [unconfirmed]:** not documented. Older standard keys start with `AIza`; the format of auth keys is not stated. Don't validate the prefix.
- The SDKs read the env vars `GEMINI_API_KEY` or `GOOGLE_API_KEY`; the latter wins if both are set.

### Free key check [doc]
```
GET https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000
x-goog-api-key: <key>
```
A 200 response looks like this:
```json
{
  "models": [
    { "name": "models/gemini-nano-banana-2.1", "baseModelId": "gemini-nano-banana-2.1", "version": "...",
      "displayName": "...", "description": "...", "inputTokenLimit": 131072, "outputTokenLimit": 32768,
      "supportedGenerationMethods": ["generateContent", "..."], "temperature": 1.0, "topP": 0.95, "thinking": true }
  ],
  "nextPageToken": "..."
}
```
- The default page size is 50 and the maximum is 1000.
- Errors: 401/403 for a bad or restricted key, 400 `failed_precondition` for billing problems.
- **Image models are not on the free tier.** Pricing shows "Not available" under Free for every Nano Banana model, so a key on a project without billing passes this check but fails on image calls.

### Models [doc]

| Name | Model id | Status | Notes |
|---|---|---|---|
| Nano Banana 2.1 | `gemini-nano-banana-2.1` | Stable, recommended | 1K/2K/4K, 14 aspect ratios incl. 1:4/4:1/1:8/8:1, Search grounding (incl. image search), thinking `minimal`/`medium`/`high` (default `medium`), up to 14 refs (≤10 objects, ≤4 characters) |
| Nano Banana 2 | `gemini-3.1-flash-image` | Stable, previous generation | Adds `512` (0.5K) size; thinking `minimal`/`high` (default `minimal`) |
| Nano Banana 2 Lite | `gemini-3.1-flash-lite-image` | Stable | Cheapest and fastest; not for multi-reference or multi-turn editing; no grounding |
| Nano Banana Pro | `gemini-3-pro-image` | Stable | Premium, always thinks; 10 aspect ratios; up to 14 refs (≤6 objects, ≤5 characters, ≤3 style) |
| Nano Banana (original) | `gemini-2.5-flash-image` | Legacy (docs say to migrate to 2 Lite); `gemini-2.5-flash-image-preview` is deprecated | Fixed sizes about 1024 px; works best with ≤3 input images |
| Imagen 4 | `imagen-4.0-generate` | **Shut down** | The `:predict` endpoint and `generate_images` no longer work |

- The earlier ids `gemini-3-pro-image-preview` and `gemini-3.1-flash-image-preview` are no longer listed. The model list shows only the non-preview ids.
- **Veo**, in one line: video only, `veo-3.1-generate-preview`, `veo-3.1-fast-generate-preview` or `veo-3.1-lite-generate-preview` via `POST /v1beta/models/{model}:predictLongRunning`, then poll the operation.
- All outputs carry a SynthID watermark.
- Gemini 3 image models always think and may produce up to 2 interim "thought" images, which are billed.

### Option A: Interactions API (recommended for new code) [doc]
The Interactions API went GA in June 2026. generateContent is now called "legacy" but "remains fully supported".

```
POST https://generativelanguage.googleapis.com/v1beta/interactions
x-goog-api-key: <key>
Content-Type: application/json
Api-Revision: 2026-05-20
```
`Api-Revision` appears in all examples. Per the May-2026 breaking-changes guide, the legacy schema was removed on 2026-06-08 and the header is now ignored. Sending it is harmless.

Text to image:
```json
{ "model": "gemini-nano-banana-2.1",
  "input": [ { "type": "text", "text": "A nano banana dish in a fancy restaurant" } ],
  "response_format": { "type": "image", "mime_type": "image/jpeg", "aspect_ratio": "16:9", "image_size": "2K" } }
```

Edit, or use multiple references. Put several image parts in `input`, up to 14:
```json
{ "model": "gemini-nano-banana-2.1",
  "input": [
    { "type": "text", "text": "An office group photo of these people, making funny faces." },
    { "type": "image", "mime_type": "image/png", "data": "<base64>" },
    { "type": "image", "mime_type": "image/png", "data": "<base64>" }
  ],
  "response_format": { "type": "image", "aspect_ratio": "5:4", "image_size": "2K" } }
```

Multi-turn edit:
```json
{ "model": "gemini-nano-banana-2.1",
  "input": "Update this infographic to be in Spanish.",
  "previous_interaction_id": "<id>",
  "response_format": { "type": "image", "mime_type": "image/jpeg", "aspect_ratio": "16:9", "image_size": "2K" } }
```

Other request fields:
- `tools: [{"type":"google_search"}]` turns on grounding. Add `"search_types":["web_search","image_search"]` for image search (2.1 and 2 only). You must display the returned `search_suggestions`.
- `generation_config: { "thinking_level": "minimal"|"medium"|"high", "seed": n, ... }`.
- `store` (default true). Stored interactions are retained 55 days on the paid tier; send `store:false` to opt out, but then `previous_interaction_id` can't refer to that interaction.
- `background: true` (requires `store`).
- `stream: true`.

`response_format` (image):
- `type: "image"`
- `mime_type`: only `image/jpeg` is listed.
- `delivery`: `inline` or `uri`. How a `uri` is fetched is [unconfirmed].
- `aspect_ratio`: `1:1`, `2:3`, `3:2`, `3:4`, `4:3`, `4:5`, `5:4`, `9:16`, `16:9`, `21:9`, `1:8`, `8:1`, `1:4`, `4:1`.
- `image_size`: `512`, `1K`, `2K`, `4K`. Use an uppercase K; `512` has no K.
- Without a `response_format`, the model returns text and images. Output size defaults to the input image's size, or 1:1 when there is no input image.

Input `image` block: `{type:"image", data | uri, mime_type, resolution?}`.
- `mime_type`: `image/png`, `jpeg`, `webp`, `heic`, `heif`, `gif`, `bmp` or `tiff`.
- `resolution`: `low`, `medium`, `high` or `ultra_high`.

Response (an Interaction):
```json
{ "id": "int_123", "object": "interaction", "model": "gemini-nano-banana-2.1",
  "status": "completed", "created": "2026-11-14T10:00:00Z", "updated": "2026-11-14T10:00:20Z",
  "steps": [
    { "type": "thought", "content": [ ... ] },
    { "type": "model_output", "content": [
        { "type": "text", "text": "Here is your image" },
        { "type": "image", "mime_type": "image/jpeg", "data": "<base64>" } ] } ],
  "usage": { "total_input_tokens": 0, "total_output_tokens": 0, "total_thought_tokens": 0, "total_tokens": 0 } }
```
- `status` values: `in_progress`, `requires_action`, `completed`, `failed`, `cancelled`, `incomplete`, `requires_continuation`.
- **Extract images** from `steps[*]` where `type=="model_output"`: take each `content[*]` with `type=="image"` and base64-decode its `data`. The SDK helper `output_image` gives the last image. Skip `thought` steps, which hold interim images.
- The exact layout of `thought` steps is [unconfirmed]; the reference page was truncated.
- Polling: send `background:true` and read `id` from the immediate reply. Then `GET /v1beta/interactions/{id}` until the status is terminal. Streaming is `GET .../interactions/{id}?stream=true&last_event_id=...`.
- Plain create calls are synchronous, which is fine for single images.

Errors: `{"error":{"code":"...","message":"..."}}`.
- Request-level codes include `authentication` (401), `payment_required` (402), `permission_denied` (403), `model_not_found` (404), `rate_limit_exceeded`/`quota_exceeded`/`too_many_requests` (429) and `service_unavailable` (503).
- Image blocks: `image_safety`, `image_prohibited_content`, `image_recitation`, `image_other`, `no_image`.
- `missing_thought_signature` also exists.

### Option B: generateContent (legacy, still supported) [doc]
The image guide's REST examples use `/v1/`; the reference lists `/v1beta/`. Both exist.
```
POST https://generativelanguage.googleapis.com/v1beta/models/gemini-nano-banana-2.1:generateContent
x-goog-api-key: <key>
Content-Type: application/json
```
```json
{
  "contents": [{
    "role": "user",
    "parts": [
      { "text": "Put this logo on the woman's shirt" },
      { "inline_data": { "mime_type": "image/png", "data": "<base64>" } },
      { "inline_data": { "mime_type": "image/jpeg", "data": "<base64>" } }
    ]
  }],
  "tools": [ { "google_search": {} } ],
  "generationConfig": {
    "responseModalities": ["TEXT", "IMAGE"],
    "imageConfig": { "aspectRatio": "16:9", "imageSize": "2K" },
    "thinkingConfig": { "thinkingLevel": "minimal", "includeThoughts": false }
  }
}
```
- `responseModalities: ["IMAGE"]` returns only images.
- `imageSize` takes `512`, `1K`, `2K` or `4K` and defaults to 1K. `gemini-2.5-flash-image` ignores it.
- Requests accept snake_case `inline_data`/`mime_type`; responses return camelCase.

Response:
```json
{ "candidates": [ { "content": { "role": "model", "parts": [
      { "inlineData": { "mimeType": "image/png", "data": "<b64>" }, "thought": true },
      { "text": "Here you go", "thoughtSignature": "<sig>" },
      { "inlineData": { "mimeType": "image/png", "data": "<b64>" }, "thoughtSignature": "<sig>" } ] },
    "finishReason": "STOP" } ],
  "usageMetadata": { "promptTokenCount": 0, "candidatesTokenCount": 0, "thoughtsTokenCount": 0, "totalTokenCount": 0 } }
```
- **Take `parts[]` with `inlineData` where `thought != true`.** Those are the final images; the output in the examples is PNG.
- For multi-turn editing, send the history back with each model part's `thoughtSignature` **exactly as received**. Final image parts and the first text part carry signatures; thought parts don't. Leaving them out can make the request fail.
- Image-related `finishReason` values: `IMAGE_SAFETY`, `IMAGE_PROHIBITED_CONTENT`, `IMAGE_RECITATION`, `IMAGE_OTHER`, `NO_IMAGE`.
- When `promptFeedback.blockReason` is set, no candidates are returned.
- Streaming is available at `:streamGenerateContent` (add `?alt=sse` for SSE [unconfirmed for images]).

### Output resolution and token table [doc]
Nano Banana 2.1, by aspect ratio:

| Ratio | 1K | 2K | 4K |
|---|---|---|---|
| 1:1 | 1024x1024 | 2048x2048 | 4096x4096 |
| 16:9 | 1376x768 | 2752x1536 | 5504x3072 |
| 9:16 | 768x1376 | 1536x2752 | 3072x5504 |
| 3:2 | 1264x848 | 2528x1696 | 5056x3392 |
| 4:3 | 1200x896 | 2400x1792 | 4800x3584 |
| 21:9 | 1584x672 | 3168x1344 | 6336x2688 |
| 8:1 | 3072x384 | 6144x768 | 12288x1536 |

Output tokens per image:
- 2.1 and 3.1 Flash: 1K = 1120, 2K = 1680, 4K = 2520. 3.1 Flash adds 512 = 747.
- 3 Pro: 1K/2K = 1120, 4K = 2000.
- 2.5 Flash Image: 1290 tokens at any ratio.
- The pricing page gives 3780 tokens for a 4K image from 2.1, which conflicts with the 2520 in the image guide.

### Pricing (paid tier only; free tier "Not available" for image models) [doc]

| Model | Image output price | Per image | Batch |
|---|---|---|---|
| `gemini-nano-banana-2.1` | $30/1M image-output tokens | ≈ $0.034 (1K), $0.050 (2K), $0.113 (4K) | Half price |
| `gemini-3.1-flash-image` | $60/1M | $0.045 (0.5K), $0.067 (1K), $0.101 (2K), $0.151 (4K) | n/a |
| `gemini-3.1-flash-lite-image` | n/a | ≈ $0.034 (1K) | ≈ $0.017 (1K) |
| `gemini-3-pro-image` | n/a | ≈ $0.134 (1K/2K), $0.24 (4K) | n/a |
| `gemini-2.5-flash-image` | n/a | ≈ $0.039 | n/a |

Grounding costs $14 per 1,000 requests after the free allowance, with a daily cap of 1,500 RPD.

### Rate limits [doc]
- Limits are per project, measured as RPM, TPM, RPD and **IPM** (images per minute, for image models). RPD resets at midnight Pacific.
- Usage tiers:
  - Free.
  - Tier 1: billing linked.
  - Tier 2: $100 paid and 3 days.
  - Tier 3: $1,000 paid and 30 days.
- Spend caps per 10 minutes: Tier 1 $10, Tiers 2–3 $200. Exceeding one returns `429 RESOURCE_EXHAUSTED`.
- **Per-model image numbers are not published in the docs.** They are shown only in AI Studio (https://aistudio.google.com/rate-limit).
- The Batch API has higher limits and up to 24 h turnaround.

### Docs used
Every page has a `.md` variant; append `.md`, and add `?generate-content=true` for the legacy-API version.
- https://aistudio.google.com/docs/image-generation (formerly ai.google.dev/gemini-api/docs/image-generation)
- https://aistudio.google.com/docs/image-generation.md?generate-content=true
- https://aistudio.google.com/docs/models
- https://aistudio.google.com/docs/models/gemini-nano-banana-2.1.md (also `gemini-3-pro-image.md` and `gemini-2.5-flash-image.md`)
- https://aistudio.google.com/api-reference/interactions-api/create-interaction.md
- https://aistudio.google.com/api-reference/generate-content
- https://aistudio.google.com/docs/interactions-breaking-changes-may-2026.md
- https://aistudio.google.com/docs/background-execution.md
- https://aistudio.google.com/docs/api-errors.md
- https://aistudio.google.com/docs/api-key.md
- https://aistudio.google.com/api-reference/models/list-models.md
- https://aistudio.google.com/docs/pricing.md
- https://aistudio.google.com/docs/rate-limits.md
- https://aistudio.google.com/docs/imagen.md
- https://aistudio.google.com/docs/veo.md
- Docs index: https://aistudio.google.com/llms.txt

---

## 3. Midjourney

**No official public API exists.** Midjourney's own documentation says so directly.
- **Community Guidelines** (updated 2026-11-05), https://docs.midjourney.com/hc/en-us/articles/32013696484109-Community-Guidelines:
  > "With a few rare exceptions that are explicitly granted, Midjourney does not provide an API, nor provide third-party apps or scripts, and automating interactions with Midjourney service is strictly prohibited according to our Terms of Service."

  Its main rules include "Unauthorized automation & third party apps are not allowed".
- **Terms of Service** (version effective 2026-05-27), https://docs.midjourney.com/hc/en-us/articles/32083055291277-Terms-of-Service:
  > "You may not use automated tools to access, interact with, or generate Assets through the Services. You may not resell or redistribute the Services or access to the Service."

  It also bars reverse engineering and allows one user per account. Midjourney may suspend or ban accounts "for any reason".
- **Official access** is the midjourney.com web app and the Discord bot, both interactive only. The "rare exceptions" are private, explicitly granted arrangements. No public application process is documented [unconfirmed beyond the quote].

**Unofficial third-party "Midjourney API" services.** These are names only. They automate Midjourney accounts (one describes itself as "Discord reverse engineering"), which **violates the ToS**. Don't integrate them.
- Apiframe
- TTAPI
- CometAPI
- Kie.ai
- Unifically
- OhMyGPT
- Apify "MidJourney API" actor
- Various RapidAPI listings
- Atlas Cloud lists "Midjourney V8.x" endpoints. Whether this is a licensed partnership is [unconfirmed]; treat it as unofficial unless Midjourney confirms it.

**Recommendation:** leave Midjourney out of the workflow nodes. At most, add a "manual step" node: show the prompt to copy, and let the user import the image they downloaded. Add an API node only if Midjourney publishes an official API.

---

## 4. Implementation notes for C# (.NET)

- Use one `HttpClient` per service with `Timeout` ≥ 3–5 minutes; OpenAI says complex prompts can take up to 2 minutes, and thinking image models are slow too.
- For SSE, read with `HttpCompletionOption.ResponseHeadersRead` and a `StreamReader`. Split on blank lines and parse the `event:` and `data:` lines.
- Base64 payloads can be tens of MB at 4K. Deserialize with `System.Text.Json` from a stream, and decode with `Convert.FromBase64String` or `Base64.DecodeFromUtf8` before writing the file.
- To detect the output type, check magic bytes rather than trusting the extension:
  - PNG: `89 50 4E 47`
  - JPEG: `FF D8 FF`
  - WebP: `RIFF....WEBP`
  - GIF: `GIF8`
- Send OpenAI edits as JSON with data URLs (`data:image/png;base64,...`). This avoids multipart; multipart works too.
- Gemini: send the key in the header, not the URL. For multi-turn generateContent, keep the whole model `parts` array, signatures included. Prefer Interactions with `previous_interaction_id` instead; the default `store:true` means Google keeps the interaction for up to 55 days.
- Codex route:
  - Start `codex app-server` (stdio JSON-RPC) as a child process.
  - Start a thread and turn with a prompt like "Generate an image: … (use $imagegen)".
  - Wait for `item/completed` with `item.type == "imageGeneration"`. Then read `savedPath`, or decode `result`.
  - Handle `failure.usageLimitExceeded`.
  - The app can't choose size or quality on this route.

## 5. Uncertain or unconfirmed
- OpenAI key prefix (`sk-` / `sk-proj-`) is not in the reference. Organization-verification behaviour on the free check is untested.
- The OpenAI Responses tool's default image model (`gpt-image-2.6-sunburst`) differs from the edits default (`gpt-image-2.5-sunburst`). The model page says Responses is "Not supported" for 2.5-flare while the guide says it is. Always set `model`.
- Which ChatGPT plans include Codex image generation is not stated on the official page.
- That `codex exec` saves to `$CODEX_HOME` is read from code, not run.
- Gemini auth-key format, image `delivery:"uri"` semantics, and per-model IPM numbers are not published.
- Gemini 4K token count for 2.1: 2520 in the image guide versus 3780 on the pricing page.
- Midjourney's "rare exceptions" have no public process, and Atlas Cloud's status is unknown.
- Model names in this file (gpt-image-2.5/2.6, gpt-6-*, gemini-nano-banana-2.1 and others) are exactly as the 2026-11-14 docs show them. They change often, so re-check `GET /v1/models` and `GET /v1beta/models` at runtime rather than hard-coding.

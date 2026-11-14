# Third-party generation services

These are the services that workflow nodes will use to make 3D models, images, sound effects, music and voices. The research was done on 2026-11-14 from each service's official docs, before anyone had keys, so nothing here has been run against a live endpoint yet. Each file marks what its docs didn't confirm.

- [3d.md](3d.md): Tripo, Tencent Hunyuan 3D, Meshy
- [images.md](images.md): OpenAI (API key, and ChatGPT plan through Codex), Google Gemini (Nano Banana), Midjourney
- [audio.md](audio.md): ElevenLabs (sound effects, music, voices), Suno

## In the app

- **Settings › Agents › Third Party** lists every service (`client/SettingsModal.ThirdParty.cs`).
  - Each service takes its key and has a **Check** button. Check makes a free call, such as a balance or model list, so nothing is made or charged.
  - Each service has toggles for what it's used for: 3D models, Images, Sound effects, Music, Voice.
  - **First choice** picks the service a workflow uses for each job when the workflow doesn't name one.
- **None of them is needed.** Everything else in Agent Orange works without any of them.
- **Keys** (`client/ThirdParty.cs`):
  - They stay on the PC where they were typed, in the local settings table, encrypted with DPAPI for the Windows user (as the server login is).
  - They never sync, never go to the agent folder, and are never given to agents. Workflows call the services themselves.
- **Service list and key checks:** `client/ThirdParty.Catalog.cs`. It also has the Tencent TC3-HMAC-SHA256 signer (`CallTencentAsync`).

## Where each service stands

| Service | For | Credential | Free key check | Job pattern |
|---|---|---|---|---|
| Tripo | 3D | `tsk_…` Bearer | `GET openapi.tripo3d.ai/v3/account/balance` | create task, poll `GET /v3/tasks/{id}` |
| Tencent Hunyuan 3D | 3D | SecretId + SecretKey + region, TC3 signed | none; a query for a job that doesn't exist (`AuthFailure.*` = bad pair) | Submit…Job, poll Query…Job (WAIT/RUN/DONE/FAIL), URLs last 24 h |
| Meshy | 3D | `msy_…` Bearer | `GET api.meshy.ai/openapi/v1/balance` | create task, poll or `/stream`; making anything needs a paid plan |
| OpenAI API | Images, Voice | `sk-…` Bearer | `GET api.openai.com/v1/models` | synchronous; base64 in `data[].b64_json` |
| OpenAI with ChatGPT | Images | a Codex login (no key) | the login is signed in | a Codex app-server turn; `imageGeneration` items carry `savedPath` |
| Google Gemini | Images, Voice | `x-goog-api-key` | `GET generativelanguage.googleapis.com/v1beta/models` | synchronous; image models need billing turned on |
| Midjourney | Images | none | none | **no public API**; its terms forbid automation |
| ElevenLabs | Sound effects, Music, Voice | `xi-api-key` | `GET api.elevenlabs.io/v1/user/subscription` | synchronous; audio bytes come back directly |
| Suno | Music | none | none | **no public API** (closed partner beta); its terms forbid automation |

**Model names change often.** Read them from each service's list endpoint at runtime where it has one, rather than hard-coding them.

# Game audio generation APIs: ElevenLabs and Suno

Researched 2026-11-14 from official docs, and from ElevenLabs' published OpenAPI spec
(`https://elevenlabs.io/docs/openapi/api-reference-2.json`). Nothing was signed up for and no key was used.
When something is **UNCONFIRMED**, the docs did not state it, or official sources disagree.

---

## 1. ElevenLabs

### 1.1 Basics

| Item | Value |
|---|---|
| Base URL | `https://api.elevenlabs.io` (default). Regional hosts: `https://api.us.elevenlabs.io`, `https://api.eu.residency.elevenlabs.io`, `https://api.in.residency.elevenlabs.io`, `https://api.sg.residency.elevenlabs.io` |
| Auth | Header `xi-api-key: <key>` on every request. No Bearer scheme is documented. Single-use tokens exist for some client-side or WebSocket uses, but they are not needed in a desktop app that keeps its key private. |
| Key format | **UNCONFIRMED.** The docs state no prefix or length. Keys seen in practice start with `sk_`, but don't validate on that. Treat the key as an opaque string. |
| Key restrictions | A key can be limited to some endpoints (scopes), given a credit quota, or limited to IP addresses or CIDR ranges. A request from an IP that isn't allowed gets `403`. **A scoped key can fail the key check below while still working for generation.** |
| Request body | `Content-Type: application/json`, except voice changer, audio isolation and stem separation, which use `multipart/form-data`. |
| SDK | Official C# SDK examples appear on the reference pages. Plain `HttpClient` is enough for everything below. |

### 1.2 Free key check

Use **`GET /v1/user/subscription`**. It returns JSON and costs no credits.

```http
GET https://api.elevenlabs.io/v1/user/subscription
xi-api-key: <key>
```

200 response (the fields worth using; the full model has more):

```json
{
  "tier": "starter",
  "character_count": 1000,
  "character_limit": 10000,
  "max_credit_limit_extension": 10000,
  "can_extend_character_limit": false,
  "voice_slots_used": 1,
  "voice_limit": 10,
  "professional_voice_limit": 0,
  "can_use_instant_voice_cloning": true,
  "can_use_professional_voice_cloning": false,
  "status": "active",
  "current_overage": { "amount": "0", "currency": "usd" },
  "next_character_count_reset_unix": 1767225600,
  "currency": "usd",
  "billing_period": "monthly_period",
  "character_refresh_period": "monthly_period"
}
```

- `character_count` is the amount used and `character_limit` is the quota for the period. These fields are named "character" but they now count **credits**, and every product draws on the same pool. Remaining = `character_limit - character_count`.
- `status` is one of `trialing`, `active`, `incomplete`, `past_due`, `free`, `free_disabled`.
- `max_credit_limit_extension` is an integer or the string `"unlimited"`, so deserialize it as a JSON element or string, not as an int.
- Other fields: `open_invoices[]`, `has_open_invoices`, `next_invoice`, `pending_change`, `cancels_immediately`, `max_voice_add_edits`, `voice_add_edit_counter`, `professional_voice_slots_used`, `professional_voice_slots_used_in_workspace`, `has_used_starter_coupon_on_account`, `has_used_creator_coupon_on_account`, `is_eligible_for_starter_promo`.

There is also **`GET /v1/user`**, which returns `user_id`, `workspace_id`, `subscription{...same fields...}`, `is_onboarding_completed`, `created_at`, `seat_type` (`workspace_admin`|`workspace_member`|`workspace_lite_member`), `first_name`, `is_api_key_hashed` and `xi_api_key_preview`.

How to read the result: `200` means the key is valid. `401` comes with `detail.code` of `invalid_api_key` or `missing_api_key`. `403` means the key is restricted: a scope or IP limit, or `insufficient_permissions`. **UNCONFIRMED:** whether a scoped key with no "user read" permission gets 401 or 403. If you need a fallback check that touches a generation scope, `GET /v1/models` also costs nothing.

### 1.3 Which endpoints return audio bytes and which return JSON

From the OpenAPI response content types:

| Endpoint | 200 body |
|---|---|
| `POST /v1/sound-generation` | **audio bytes** (`application/octet-stream`) |
| `POST /v1/text-to-speech/{voice_id}` | **audio bytes** |
| `POST /v1/text-to-speech/{voice_id}/stream` | **audio bytes, chunked** |
| `POST /v1/text-to-speech/{voice_id}/with-timestamps` | JSON (`audio_base64` + alignment) |
| `POST /v1/text-to-speech/{voice_id}/stream/with-timestamps` | stream of JSON chunks (base64 audio + alignment) |
| `POST /v1/text-to-dialogue` (+ `/stream`, `/with-timestamps`, `/stream/with-timestamps`) | same pattern as TTS |
| `POST /v1/music` | **audio bytes** |
| `POST /v1/music/stream` | **audio bytes, chunked** |
| `POST /v1/music/detailed` | multipart/mixed: JSON metadata + audio (see 1.6) |
| `POST /v1/music/plan` | JSON (composition plan), **costs no credits** |
| `POST /v1/music/stem-separation` | **ZIP bytes** |
| `POST /v1/music/upload` | JSON (`song_id`, ...) |
| `POST /v1/music/video-to-music` | audio bytes (+ `song-id` header, per the changelog) |
| `POST /v1/voice-changer/{voice_id}` (+ `/stream`) | **audio bytes** |
| `POST /v1/text-to-voice/design` | JSON (base64 previews) |
| `POST /v1/text-to-voice` | JSON (new `voice_id`) |
| `GET /v1/text-to-voice/{generated_voice_id}/stream` | audio stream of a preview |
| `POST /v1/audio-isolation` (+ `/stream`) | audio (the spec says JSON, which is very likely wrong; see 1.9) |
| `GET /v2/voices`, `GET /v1/models`, `GET /v1/user[/subscription]` | JSON |

**Everything is synchronous.** The HTTP request stays open until the audio is ready. ElevenLabs has no job IDs and no polling for these products. Set long `HttpClient.Timeout` values: music can take a minute or more, and stem separation "might have high latency, depending on the length of the audio file".

**Streaming:** the OpenAPI spec labels stream responses `text/event-stream`, but the streaming guide says they are "raw audio bytes (e.g., MP3 data) directly over HTTP using chunked transfer encoding". Read them with `HttpCompletionOption.ResponseHeadersRead` and copy the stream. They are **not** SSE frames. The exception is `*/stream/with-timestamps`, a "stream of JSONs containing audio as a base64 encoded string"; the exact framing (newline-delimited) is **UNCONFIRMED**.

### 1.4 `output_format` (query string, not body)

The format is `codec_samplerate[_bitrate]`. The default is `mp3_44100_128`, except music, where the default is `auto`.

- MP3: `mp3_22050_32`, `mp3_24000_48`, `mp3_44100_32`, `mp3_44100_64`, `mp3_44100_96`, `mp3_44100_128`, `mp3_44100_192`. **Music only:** `mp3_48000_128`, `mp3_48000_192`, `mp3_48000_240`, `mp3_48000_320`
- PCM (raw signed 16-bit little-endian mono, **no header**): `pcm_8000`, `pcm_16000`, `pcm_22050`, `pcm_24000`, `pcm_32000`, `pcm_44100`, `pcm_48000`
- WAV (with header): `wav_8000`, `wav_16000`, `wav_22050`, `wav_24000`, `wav_32000`, `wav_44100`, `wav_48000`. These are listed for **sound-generation, text-to-speech, text-to-dialogue and voice-changer**. They are **not** in the enum for `/v1/music`, `/v1/music/stem-separation` or `/v1/text-to-voice/design`, so for music use `pcm_48000`/`pcm_44100` and write the WAV header yourself.
- Opus: `opus_48000_32`, `opus_48000_64`, `opus_48000_96`, `opus_48000_128`, `opus_48000_192`
- Telephony: `ulaw_8000`, `alaw_8000`
- Music: `auto` gives `mp3_44100_128` for `music_v1` and `mp3_48000_192` for `music_v2`/`music_v2_5`.

Plan gates: `mp3_44100_192` needs **Creator+**. PCM or WAV at 44.1 kHz needs **Pro+**. **UNCONFIRMED:** whether 48 kHz PCM/WAV has the same gate. The docs mention only 44.1 kHz, so expect it to apply.
The PCM sample format (s16le mono) comes from general ElevenLabs knowledge; the reference pages don't spell it out. **Verify it** against the first response.

Common query parameter: `enable_logging=false` turns on zero-retention mode and is enterprise only. Leave it at its default.

### 1.5 Sound effects: `POST /v1/sound-generation`

```http
POST https://api.elevenlabs.io/v1/sound-generation?output_format=wav_48000
xi-api-key: <key>
Content-Type: application/json

{
  "text": "Heavy wooden door creaks open slowly in a stone dungeon, one-shot",
  "model_id": "eleven_text_to_sound_v3",
  "duration_seconds": 3.5,
  "prompt_influence": 0.5,
  "loop": false
}
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `text` | string, required | none | The prompt |
| `model_id` | enum | `eleven_text_to_sound_v2` | `eleven_text_to_sound_v2` (**deprecated, removed 2027-03-01**) or `eleven_text_to_sound_v3`. **Always send v3 explicitly.** |
| `duration_seconds` | double or null | null (the model chooses) | API reference: **0.5 to 30**. The capability page says 0.1 to 30. Use the API range. |
| `prompt_influence` | double or null | 0.3 | 0 to 1. Higher values follow the prompt more literally and vary less. |
| `loop` | bool | false | Seamless loop for ambiences. **UNCONFIRMED:** whether looping works with both models. The capability page says WAV 48 kHz is for *non-looping* effects, so request MP3 or PCM for loops if WAV is refused. |

- Response: raw audio bytes in `output_format`.
- Cost: **40 credits per second when `duration_seconds` is set**. The docs give no cost for auto duration.
- Concurrency: each generation counts as one concurrent request while it runs (see 1.11).
- Prompt tips from the docs: describe the action, material and setting ("Footsteps on gravel approaching slowly"). Chain events with "then". Use words like "impact", "whoosh", "one-shot" and "loop". For musical loops, give BPM and key.

### 1.6 Music (Eleven Music)

The Music API is for **paid subscribers only**, and the Free plan's music concurrency is 0.

Models: `music_v1` is still the API default but is **deprecated and removed 2027-01-31**. `music_v2` adds Audio Reference, long-form section-by-section writing and in-track SFX. `music_v2_5` is the best available. **Send `model_id` explicitly, preferably `music_v2_5`.**

#### 1.6.1 Compose: `POST /v1/music` (returns audio bytes)

Simple prompt:
```json
{
  "prompt": "Looping 8-bit dungeon exploration theme, 110 BPM, minor key, chiptune, no vocals",
  "music_length_ms": 60000,
  "model_id": "music_v2_5",
  "force_instrumental": true
}
```

| Field | Type | Default | Notes |
|---|---|---|---|
| `prompt` | string or null | none | max 4100 chars. Can't be combined with `composition_plan`. |
| `composition_plan` | object or null | none | Can't be combined with `prompt`. The shape depends on the model (below). |
| `music_length_ms` | int or null | the model chooses | 3000 to 600000 (reference). The capability page says the maximum is 5 minutes. **Conflict; assume 300000 is the safe maximum.** Used only with `prompt`. |
| `model_id` | enum | `music_v1` | `music_v1`, `music_v2`, `music_v2_5` |
| `force_instrumental` | bool | false | Used only with `prompt`. Guarantees no vocals. |
| `seed` | int or null | none | **Not allowed together with `prompt`.** Reproducibility is not guaranteed. |
| `finetune_id` | string or null | none | Music finetunes (`/v1/music/finetunes`) |
| `respect_sections_durations` | bool | true | Only for `music_v1` with a plan. v2 always enforces durations. |
| `store_for_inpainting` | bool | false | Keep the song so it can be reused or edited by `song_id` |
| `sign_with_c2pa` | bool | false | MP3 only |

Composition plan for **v2 / v2_5** (`CompositionPlan`):
```json
{
  "model_id": "music_v2_5",
  "composition_plan": {
    "chunks": [
      {
        "text": "[Intro]",
        "duration_ms": 8000,
        "positive_styles": ["orchestral", "tense strings", "low brass"],
        "negative_styles": ["vocals"],
        "context_adherence": "high"
      },
      {
        "text": "[Battle]",
        "duration_ms": 30000,
        "positive_styles": ["epic orchestral", "taiko drums", "fast 150 BPM"]
      }
    ]
  }
}
```
- A `GenerationChunk` has `text` (optional: a bracketed section name, lyric lines, and inline directions in `{curly braces}`), `duration_ms` (3000 to 120000), `positive_styles` (required), `negative_styles`, `context_adherence` (`low`|`medium`|`high`, default `high`), `conditioning_ref` (an AudioRefChunk) and `condition_strength` (`low`|`medium`|`high`|`xhigh`).
- An `AudioRefChunk` is `{ "song_id": "...", "range": { "start_ms": 0, "end_ms": 10000 } }`. It reuses audio from a stored or uploaded song.

Composition plan for **v1** (`MusicPrompt`):
```json
{
  "positive_global_styles": ["pop", "rock"],
  "negative_global_styles": ["metal"],
  "sections": [
    {
      "section_name": "Verse 1",
      "positive_local_styles": ["pop"],
      "negative_local_styles": [],
      "duration_ms": 10000,
      "lines": ["Verse 1 lyrics"]
    }
  ]
}
```
Section limits: `section_name` is 1 to 100 chars, `duration_ms` is 3000 to 120000, and `lines` holds at most 30 lines of at most 200 chars each. `source_from {song_id, range, negative_ranges}` is optional and used for inpainting.

#### 1.6.2 Other music endpoints

- **`POST /v1/music/plan`**: turns a prompt into a plan and **costs no credits**, though it is rate limited by tier. Body: `prompt` (required), `music_length_ms`, `source_composition_plan`, `model_id`. It returns the plan JSON (`MusicPrompt` or `CompositionPlan`), which you can edit and pass back as `composition_plan`. Useful for a "preview structure" node.
- **`POST /v1/music/stream`**: the same body as `/v1/music`, minus `respect_sections_durations` and `sign_with_c2pa`. Returns chunked audio bytes.
- **`POST /v1/music/detailed`**: the same body plus `with_timestamps` (word timestamps) and `with_waveform_visual`. The response is **multipart/mixed**, with a JSON part (`composition_plan`, `song_metadata {title, description, genres, languages, is_explicit}`, and optional timestamps) and a binary audio part. The song ID is in the **`song-id` response header**. **UNCONFIRMED:** the exact part order and boundary handling; parse it with `MultipartReader` using the boundary from `Content-Type`. `/v1/music/detailed/stream` also exists.
- **`POST /v1/music/stem-separation`** (multipart): `file` (required), `stem_variation_id` = `two_stems_v1` | `six_stems_v1` (default `six_stems_v1`), `sign_with_c2pa`, and the `output_format` query. It returns a **ZIP** with one file per stem. The docs don't name the stems. **UNCONFIRMED:** whether it accepts only ElevenLabs-generated music or any audio. The docs say "an audio file".
- **`POST /v1/music/upload`** (multipart): `file`, `extract_composition_plan` (a model id string such as `"music_v2_5"`; `true`/`false` is deprecated), `with_timestamps`, `with_waveform_visual`. It returns JSON with `song_id`. Uploads cost the same as a generation and are copyright-scanned; if a match is found, half the cost is still charged.
- **`POST /v1/music/video-to-music`**: video in, music out. Not relevant to this app.
- Finetunes: `GET/POST /v1/music/finetunes` and `GET/PATCH/DELETE /v1/music/finetunes/{finetune_id}`.

Output formats per the music capability page: MP3 at 44.1 kHz/128 to 192 kbps, and WAV. Through the API, ask for `pcm_48000` and wrap it yourself (see 1.4).

### 1.7 Text to speech

#### Voices: `GET /v2/voices`
Query: `page_size` (default 10, max 100), `next_page_token`, `search`, `sort` (`created_at_unix`|`name`), `sort_direction`, `voice_type` (`personal`|`community`|`default`|`workspace`|`non-default`|`non-community`|`saved`), `category` (`premade`|`cloned`|`generated`|`professional`), `gender`, `age`, `accent`, `language`, `use_cases`, `voice_ids`, `include_total_count` (default true; set it to false for speed).

```json
{
  "voices": [
    {
      "voice_id": "JBFqnCBsd6RMkjVDRZzb",
      "name": "George",
      "category": "premade",
      "labels": { "accent": "british", "gender": "male" },
      "description": "...",
      "preview_url": "https://...mp3",
      "is_owner": false
    }
  ],
  "has_more": true,
  "total_count": 42,
  "next_page_token": "..."
}
```
Page through results with `next_page_token` while `has_more` is true. The first page can contain more than `page_size` voices because default voices are added. Get one voice with `GET /v1/voices/{voice_id}`. The shared community library is `GET /v1/shared-voices` ("voice-library/get-shared").

#### Models: `GET /v1/models`
Returns a JSON array of `{ model_id, name, description, can_do_text_to_speech, can_do_voice_conversion, can_use_style, can_use_speaker_boost, maximum_text_length_per_request, languages:[{language_id,name}], model_rates:{character_cost_multiplier, cost_discount_multiplier}, concurrency_group, token_cost_factor, requires_alpha_access, ... }`. Filter it on `can_do_text_to_speech` or `can_do_voice_conversion`.

Current TTS model IDs (from the models overview page):

| model_id | Max chars/request | Notes |
|---|---|---|
| `eleven_v4` | 10,000 | Flagship. "Ideal for gaming and animation due to its emotional range". Supports Text to Dialogue. |
| `eleven_v4_turbo` | not stated | ~100 ms. Recommended for expressive interactive characters. |
| `eleven_v3` | 5,000 | Previous generation, still available. Audio tags such as `[laughs]`. Default for text-to-dialogue. |
| `eleven_v3_conversational` | not stated | ~165 ms |
| `eleven_multilingual_v2` | 10,000 | **The API default for TTS** when `model_id` is omitted. 29 languages. |
| `eleven_flash_v2_5` | 40,000 | ~100 ms, cheapest (half price) |
| `eleven_flash_v2` | 30,000 | English only |
| `eleven_turbo_v2_5`, `eleven_turbo_v2` | none | Deprecated; use Flash |

#### Create speech: `POST /v1/text-to-speech/{voice_id}` (returns audio bytes)
```http
POST https://api.elevenlabs.io/v1/text-to-speech/JBFqnCBsd6RMkjVDRZzb?output_format=wav_44100
xi-api-key: <key>
Content-Type: application/json

{
  "text": "Halt! Who goes there?",
  "model_id": "eleven_v4",
  "voice_settings": { "stability": 0.5, "similarity_boost": 0.75, "style": 0, "speed": 1.0, "use_speaker_boost": true },
  "seed": 1234,
  "apply_text_normalization": "auto"
}
```
Body fields: `text` (required), `model_id` (default `eleven_multilingual_v2`), `language_code` (ISO 639-1; not for multilingual_v2), `voice_settings` {`stability` 0 to 1, default 0.5; `similarity_boost` 0 to 1, default 0.75; `style`, default 0; `speed`, default 1.0; `use_speaker_boost`, default true; `clone_strength` 0 to 1, PVC on v4 only}, `seed` (0 to 4294967295), `previous_text`, `next_text`, `previous_request_ids` / `next_request_ids` (max 3), `pronunciation_dictionary_locators` [{`pronunciation_dictionary_id`, `version_id`}] (max 3), `apply_text_normalization` (`auto`|`on`|`off`), `audio_effects` {`filter_preset_id`, `distance`, `environment_id`, `background_noise_id`, `send_level`, `pan`}, and `use_pvc_as_ivc`.
Query: `output_format`, `enable_logging`, and `optimize_streaming_latency` (0 to 4, deprecated).

Variants:
- `POST /v1/text-to-speech/{voice_id}/stream` returns chunked audio bytes.
- `POST /v1/text-to-speech/{voice_id}/with-timestamps` returns JSON, which is useful for lip-sync or subtitles:
  ```json
  {
    "audio_base64": "<base64>",
    "alignment": { "characters": ["H","i"], "character_start_times_seconds": [0,0.1], "character_end_times_seconds": [0.1,0.2] },
    "normalized_alignment": { "characters": [], "character_start_times_seconds": [], "character_end_times_seconds": [] }
  }
  ```
- `POST /v1/text-to-speech/{voice_id}/stream/with-timestamps` streams JSON chunks of the same kind.
- A WebSocket exists at `wss://api.elevenlabs.io/v1/text-to-speech/{voice_id}/stream-input`, plus a multi-context variant. Not needed for offline asset generation.

Response headers: the reference pages document **none**. **UNCONFIRMED:** header names such as `request-id`, `character-cost` or `x-character-count`. Don't depend on them; read the subscription again to see usage.

#### Multi-voice dialogue: `POST /v1/text-to-dialogue` (returns audio bytes)
```json
{
  "inputs": [
    { "text": "[laughs] Guess what I just found.", "voice_id": "VOICE_A" },
    { "text": "[excited] Tell me!", "voice_id": "VOICE_B" }
  ],
  "model_id": "eleven_v3",
  "settings": { "stability": 0.5, "similarity": 0.75 },
  "seed": 42,
  "apply_text_normalization": "auto"
}
```
Limits: up to 10 unique voices, and keep the total text at or below about 2,000 chars per request. The default model is `eleven_v3`; `eleven_v4` is supported. Other fields: `language_code`, `previous_text`/`future_text` (max 100 chars), `previous_request_ids`/`next_request_ids`, `pronunciation_dictionary_locators`, `audio_effects`. Reverb or non-zero pan produces stereo, which is rejected with 400 for `pcm_*`/`ulaw_*`/`alaw_*`. Variants: `/stream`, `/with-timestamps`, `/stream/with-timestamps`, and the WebSocket `/stream-input`.

### 1.8 Voice design and voice changer (character voices)

**Design: `POST /v1/text-to-voice/design`** (returns JSON with base64 previews; the `output_format` query is optional and has no WAV)
```json
{
  "voice_description": "Gravelly old dwarven blacksmith, deep voice, Scottish accent, gruff but kind",
  "model_id": "eleven_ttv_v3",
  "text": "Aye, that blade'll need a proper forging. Come back at dawn and I'll have it singing for ye, lad. Don't be late now.",
  "auto_generate_text": false,
  "loudness": 0.5,
  "guidance_scale": 5,
  "seed": 7
}
```
Fields: `voice_description` (required), `model_id` (`eleven_multilingual_ttv_v2` default, or `eleven_ttv_v3`), `text` (100 to 1000 chars, or null) **or** `auto_generate_text: true`, `loudness` (-1 to 1; 0 is about -24 LUFS), `seed`, `guidance_scale` (default 5), `stream_previews` (when true, only IDs come back; stream each with `GET /v1/text-to-voice/{generated_voice_id}/stream`), `should_enhance`, `quality`, `reference_audio_base64` and `prompt_strength` (ttv_v3 only), `remixing_session_id`, `remixing_session_iteration_id`.
Response:
```json
{
  "previews": [
    { "audio_base_64": "<base64>", "generated_voice_id": "abc123", "media_type": "audio/mpeg", "duration_secs": 6.2, "language": "en" }
  ],
  "text": "Aye, that blade'll ..."
}
```
Note the field name is **`audio_base_64`**, with an underscore before 64, unlike TTS's `audio_base64`. `409` means a generation is already in progress; retry it.

**Save the chosen preview: `POST /v1/text-to-voice`**
```json
{ "voice_name": "Dwarf Smith", "voice_description": "Gravelly old dwarven blacksmith...", "generated_voice_id": "abc123", "labels": { "game": "proj1" } }
```
This returns a voice object with `voice_id`, which you then use in TTS. Saved voices take up voice slots (`voice_limit` in the subscription). `POST /v1/text-to-voice/{voice_id}/remix` also exists. The older `POST /v1/text-to-voice/create-previews` is still in the spec.

**Voice changer (speech to speech): `POST /v1/voice-changer/{voice_id}`** (multipart; returns audio bytes)
Form fields: `audio` (file, required), `model_id` (for example `eleven_multilingual_sts_v2` or `eleven_english_sts_v2`; no default is stated, so always send it), `voice_settings` (a **JSON string**), `seed`, `remove_background_noise` (bool), `file_format` (`pcm_s16le_16` | `other`). Query: `output_format` (WAV allowed). A streaming variant is at `/v1/voice-changer/{voice_id}/stream`. Use it to turn a temp line you recorded into a character voice while keeping the delivery. Price: $0.12/min.

**Audio isolation** (in one line): `POST /v1/audio-isolation` (multipart `audio`, optional `file_format`) strips background noise from speech; `/stream` and `/history` variants exist. The spec declares a JSON response, but the endpoint is documented to stream audio, so **treat the response as audio bytes and check `Content-Type`** (UNCONFIRMED).

### 1.9 Errors

Every error has the shape:
```json
{ "detail": { "type": "validation_error", "code": "invalid_parameters", "message": "...", "status": "invalid_parameters", "request_id": "3c80...", "param": "keyterms" } }
```
Some 422 responses use the older FastAPI shape `{"detail":[{"loc":[...],"msg":"...","type":"..."}]}`. **Handle both**: `detail` can be an object or an array.

| HTTP | type | codes worth handling |
|---|---|---|
| 400 | `validation_error` / `invalid_request` | `text_too_long`, `invalid_voice_id`, `unsupported_model`, `invalid_output_format`, `invalid_audio`, `request_too_large` |
| 401 | `authentication_error` | `invalid_api_key`, `missing_api_key` |
| 402 | `payment_required` | `insufficient_credits` |
| 403 | `authorization_error` | `feature_not_available`, `subscription_required`, `model_access_denied`, `insufficient_permissions` (also returned for an IP that isn't allowlisted) |
| 404 | `not_found` | `voice_not_found`, `song_not_found`, `model_not_found` |
| 409 | `conflict` | `already_running` (voice design) |
| 429 | `rate_limit_error` | `rate_limit_exceeded`, `concurrent_limit_exceeded` (the help center calls this `too_many_concurrent_requests`), `system_busy` |
| 500/503 | `internal_error` / `service_unavailable` | `maintenance` |

For 429 and 503, retry with exponential backoff. ElevenLabs first queues requests over the concurrency limit (about 50 ms) and returns 429 only if you are still over it. An ElevenLabs blog post mentions `current-concurrent-requests` / `maximum-concurrent-requests` response headers; **this is UNCONFIRMED in the API reference**.

### 1.10 Concurrency limits (models overview page)

| Plan | TTS Multilingual v2 | TTS Flash | Music | SFX |
|---|---|---|---|---|
| Free | 2 | 4 | **0** | 4 |
| Starter | 3 | 6 | 2 | 4 |
| Creator | 5 | 10 | 2 | 6 |
| Pro | 10 | 20 | 2 | 6 |
| Scale / Business | 15 | 30 | 5 | 8 |
| Enterprise | elevated | elevated | highest | elevated |

The docs give no per-minute request rate. `/v1/music/plan` is "rate limited depending on your tier", with no numbers given. **The app should cap parallel requests per product a little below these limits** (for example a `SemaphoreSlim` for each of SFX, music and TTS).

### 1.11 Pricing and licence

- Plans per the API pricing page: Free $0, Starter $6/mo, Creator $22/mo, Pro $99/mo, Scale $299/mo, Business $990/mo. Overage rates: TTS $0.08 per 1K chars for v4/v3/multilingual_v2 and $0.04 per 1K for Flash/v4 Turbo/v3 Conversational. SFX $0.12/min (Free includes about 8 generations, Starter about 150). Music $0.15/min (the pricing page lists 3 min for Free, but the music docs say the Music API is for paid subscribers and the Free plan's music concurrency is 0; my guess is that the Free minutes are for the web app only, UNCONFIRMED). Voice changer $0.12/min. SFX credits: 40 credits per second when a duration is set.
- **Licence:** the Free plan has **no commercial licence**, and anything shared must credit "elevenlabs.io" or "11.ai" in the title ("Eleven Music" for music). **All paid plans include a commercial licence**, except for features marked Beta. Eleven Music is described as "cleared for nearly all commercial uses", gaming included. Marketing pages call ElevenLabs sound effects "royalty free", which conflicts with the Free-plan rule above, so assume a paid plan is needed for shipping.

### 1.12 ElevenLabs sources
- Authentication: https://elevenlabs.io/docs/api-reference/authentication
- Sound effects: https://elevenlabs.io/docs/api-reference/text-to-sound-effects/convert · capability page: https://elevenlabs.io/docs/overview/capabilities/sound-effects
- Music: https://elevenlabs.io/docs/api-reference/music/compose · /music/stream · /music/compose-detailed · /music/create-composition-plan · /music/separate-stems · /music/upload · capability page: https://elevenlabs.io/docs/overview/capabilities/music · index: https://elevenlabs.io/docs/api-reference/music/llms.txt
- TTS: https://elevenlabs.io/docs/api-reference/text-to-speech/convert · /stream · /convert-with-timestamps
- Dialogue: https://elevenlabs.io/docs/api-reference/text-to-dialogue/convert
- Voices: https://elevenlabs.io/docs/api-reference/voices/search · Models: https://elevenlabs.io/docs/api-reference/models/list · https://elevenlabs.io/docs/overview/models
- Voice design: https://elevenlabs.io/docs/api-reference/text-to-voice/design · /text-to-voice/create
- Voice changer: https://elevenlabs.io/docs/api-reference/speech-to-speech/convert · Audio isolation: https://elevenlabs.io/docs/api-reference/audio-isolation/convert
- User / key check: https://elevenlabs.io/docs/api-reference/user/subscription/get · https://elevenlabs.io/docs/api-reference/user/get
- Streaming: https://elevenlabs.io/docs/api-reference/streaming · Errors: https://elevenlabs.io/docs/eleven-api/resources/errors · 429: https://elevenlabs.io/docs/help-center/technical/api-error-code-429
- Pricing: https://elevenlabs.io/pricing/api · Licence: https://elevenlabs.io/docs/help-center/legal/can-i-publish-the-content-i-generate-on-the-platform
- OpenAPI: https://elevenlabs.io/docs/openapi/api-reference-2.json (and `api-reference.json`)

---

## 2. Suno

### 2.1 Status: no public API (as of 2026-11-14)

- **Suno has no public, self-serve, documented HTTP API.**
- **Developer API, partner-only beta.** On 2026-07-01, CPO Jack Brody said Suno is "beginning to explore a developer API, starting with a curated group of partners". Interest is taken through a Typeform intake form; MBW did not publish its URL. No timeline has been given.
- **`https://platform.suno.com`** is a Suno-run sign-in page ("Make music with the Suno API … simple REST API … original songs, covers, and mashups"). It is © 2026 Suno, Inc., and sign-in is by Google or an emailed code. **No public docs, endpoints, base URL, key format, pricing or rate limits are visible**, and `/docs` is behind the same sign-in. Suno told MBW (2026-10-30) the API "remains in beta" and will launch with select partners.
- **So auth, endpoints, request fields, polling, audio URLs, formats, a key check and pricing cannot be documented. They are not public.** If the user is admitted to the beta, the docs inside platform.suno.com will be the source.

### 2.2 Official alternative: Suno MCP (beta, 2026-10-28)

- Server URL: `https://api.suno.com/mcp` (HTTP transport). It is meant for MCP clients such as Claude, ChatGPT, Gemini, Cursor and Claude Code (`claude mcp add --transport http suno https://api.suno.com/mcp`).
- Auth: the user signs in to Suno and grants access interactively. No API key is involved.
- Capabilities: shape lyrics and style through chat, then generate. Start from uploaded audio. Make covers, extensions and mashups from your library. Songs are saved to the user's Suno library and use the plan's credits. Available on all plans, including Free, with early-access sign-up at https://suno.com/mcp.
- **The docs don't publish tool names or schemas, and don't say whether a custom app (rather than an AI assistant) may connect.** A C# desktop app acting as its own MCP client is **not documented as supported**. Don't build on it without Suno's confirmation.

### 2.3 Terms on automation (Terms of Service; revised 2026-08-10, effective 2026-09-03)

- Prohibited: "engage in or use any data mining, robots, scraping, or similar data gathering or extraction methods". Also prohibited: obtaining content "through any means not intentionally made available or provided for through the Service", circumventing blocking, and reverse engineering. "Obtaining a copy of an Output by any means other than a download channel made available by Suno is prohibited."
- Output rights: **Free/Basic** tiers get "lawful, personal and non-commercial purposes" only. **Pro/Premier** get Suno's rights in Output assigned to them, with commercial use allowed only for Output with "a permitted download" within the tier's download allotment. Suno doesn't warrant that copyright vests. Remixes are non-commercial on every tier.
- Pricing (suno.com/pricing): Free gets 50 credits a day, model v6-mini, no downloads, no commercial rights. Pro is $8/mo promo ($10 list), 2,500 credits/mo, 20 downloads/mo, v6 and v6-wild, 2 stem types. Premier is $24/mo promo ($30 list), 10,000 credits/mo, 60 downloads/mo, 3 stem types. These are consumer plans; nothing says they apply to API use.

### 2.4 Unofficial wrappers (**UNOFFICIAL. Not endorsed by Suno. Don't use or recommend.**)

Listed by name only, so they can be recognised:
- GitHub: `gcui-art/suno-api` (and forks such as `oldcai/suno-api` and `rhdeck/suno-api`). They run on a logged-in user's session cookie plus a CAPTCHA-solving service. That is automated access through means Suno did not make available, which **conflicts with the ToS clauses above**.
- Paid third-party resellers selling "Suno API" access, such as sunoapi.org, aimusicapi.ai, musicapi.ai, Sunor, Unifically, Kunavo and Sonilo. None are Suno partners as far as public sources show, and their access method isn't disclosed.

**Recommendation:** in AgentOrange, offer no Suno node until Suno grants official access (the platform.suno.com beta). Until then, use ElevenLabs Music for music nodes.

### 2.5 Can Suno make sound effects?

Partly, and only in the web app. **Suno Sounds** (beta, released 2026-01-27, **Pro or Premier only**) generates "individual sound effects, instrument samples, ambient noises", either as a **One Shot** or a seamless **Loop** (you set key and BPM). No length limit is published; you put the duration in the prompt. It isn't documented as reachable through the MCP or the API. Otherwise Suno is a music and song generator.

### 2.6 Suno sources
- MBW, API exploration (2026-07-02): https://www.musicbusinessworldwide.com/suno-explores-developer-api-seeking-apps-that-unlock-experiences-generative-music-makes-possible-for-the-first-time/
- MBW, MCP and API portal still in beta (2026-10-30): https://www.musicbusinessworldwide.com/sunos-new-mcp-tool-lets-users-make-songs-inside-chatgpt-claude-and-gemini-as-its-developer-api-gets-a-sign-in-portal-but-remains-in-beta/
- API portal (sign-in only): https://platform.suno.com
- MCP: https://suno.com/mcp · https://suno.com/release-notes/suno-mcp-beta · https://help.suno.com/en/articles/14486145 · https://help.suno.com/en/articles/14551425
- Terms: https://suno.com/terms · Pricing: https://suno.com/pricing
- Sounds: https://help.suno.com/en/articles/10625537 · https://suno.com/release-notes/make-loops-and-samples-from-scratch-with-sounds

---

## 3. Implementation notes for C# workflow nodes

1. One `HttpClient` with `BaseAddress = https://api.elevenlabs.io/` and a default header `xi-api-key`. Allow the regional host to be configured.
2. Generation calls: `SendAsync(req, HttpCompletionOption.ResponseHeadersRead)`. If the status is 2xx and `Content-Type` isn't JSON, copy the stream to a file. Otherwise parse `detail`, which may be an object or an array.
3. Pick the file extension from the `output_format` you asked for (`mp3_*` → .mp3, `wav_*` → .wav, `pcm_*` → raw, so add a 44-byte RIFF header for 16-bit mono at that rate, `opus_*` → .opus or .ogg; verify which container).
4. Timeouts: about 60 s for SFX and TTS, at least 5 min for music and stems. No polling.
5. Per-product `SemaphoreSlim` sized from the plan (1.10). Back off on 429 and 503.
6. Key check node: `GET v1/user/subscription`. Show `tier`, `status`, remaining credits (`character_limit - character_count`) and the reset time (`next_character_count_reset_unix`).
7. Always send `model_id` explicitly (`eleven_text_to_sound_v3`, `music_v2_5`, `eleven_v4` / `eleven_flash_v2_5`), because the server defaults are deprecated models.

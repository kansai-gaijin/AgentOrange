# Skill tests

Runs a fresh agent on a task per skill, the way an Agent Orange session is set up. The person
grades the results on an outputs page. A retest reruns one skill with the person's own prompt.

The code is here. A run's data lives outside the repo, in `SKILLTESTS_DATA` (default
`D:\blenderskills`). See `paths.py`.
- `sktest\fx`: practice files for the first round.
- `sktest\refs*`: reference sets for retests (pictures, heads, meshes). They're the person's art, so
  they stay out of this public repo. Copy them from the last machine, or ask the person.
- `sktest\run`: one folder per run, plus its `.jsonl` log and `.json` result.
- `sktest\results`, `sktest\outpage`: judge grades and the outputs page's data.
- `skdata\`: a copy of an Agent Orange agent folder, as a session gets it.
  - `agent\providers\claude\<id>\agent-orange` is the plugin with the skills the runs load.
  - `agent\briefings\` holds the briefings.
  - Make it from a machine where Agent Orange has set up a Claude session (`~/.agent-orange`).
  - After changing a skill, copy the skill from `client\Skills` into
    `skdata\agent\providers\claude\<id>\agent-orange\skills\<name>`, so the next run gets it.
- `BLENDER` names Blender's executable (default: the Steam install). The runs need Blender 5.3 and the
  `claude` CLI, logged in.

Keep runs off C: on a full disk. Each art run writes hundreds of MB of renders and blends.

## Running

| Command | What it does |
|---|---|
| `python runner.py --group blender --workers 3` | The first round: every skill's task (`tests.py`), then a fresh judge grades it. |
| `python retest.py <fable\|opus> <effort> <skill> <n> prompts\<file> <refs...>` | One retest, to `run\<skill>_retest<n>`. Set `UNLIMITED=1` for no time limit, `TARGET_SKILL` when it should use another skill than the one it's filed under. |
| `python resume_retest.py <run name> <session id> <minutes so far> [prompt] [effort]` | Carries on a retest whose process was stopped from outside. |
| `python outputs.py` | Builds the outputs page's data from the runs. Publish `outpage` as the page's files. |

Start long runs detached (PowerShell `Start-Process`), not under a tool with a 2-hour limit: killing
the runner kills the agent too.

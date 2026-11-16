# Dogfooding log

What got in the way while using Agent Orange to make the games, and what was done about it. Newest first.
Anything not fixed on the spot is a GitHub issue, not a line here.

## 2026-11-15 · Unreal 5.9: the game's project, made and opened from Agent Orange

With Unreal set as the engine and nothing in the folder, the project page had nothing to offer: no
way to make the Unreal project or open Unreal, and an agent couldn't reach the engine at all.

- **Create the Unreal project** (`UnrealProject.cs`, `MainWindow.Unreal.cs`): shown on the project
  page while the folder has no .uproject, with the installed engines to pick from (found from the
  Epic launcher's list and Program Files). It does what Unreal's own wizard does with its Blank C++
  template (by the template's TemplateDefs.ini), turns on Unreal MCP and All Toolsets, sets Unreal's
  MCP server to start on its own on Agent Orange's port (8666), adds Unreal's folders to
  .gitignore, and builds the editor's copy of the code, so Unreal never asks to. About 3 minutes.
  The page then says "Unreal Engine 5.9" and offers **Open in Unreal Engine**.
- **Agents open and use the game's engine** like the creative tools: open_tool opens the project in
  Unreal and waits up to ten minutes; Unreal's own MCP server (5.9's is built in, at /mcp) gives
  around 128 toolsets through its list_toolsets, describe_tools and call_tool.
- Agent Orange had 5.8's MCP address for Unreal (port 8666 at /uemcp). 5.9's is /mcp, port 8000
  unless told otherwise, and only started when asked, so Agent Orange now tells it both.
- **Material Maker's "crash" was its rescue prompt**, there because I'd force-closed it with
  unsaved test materials. Tools get closed the normal way, never killed.
- In a dev build only, `dotnet watch` crashing takes down whatever the app started, Unreal included.

## 2026-11-15 · Blender through Blender Lab's official Blender MCP

The user asked whether Agent Orange used the official Blender MCP. It didn't: it had a bridge of its
own. Now it uses Blender Lab's (`BlenderMcp.cs`), and the bridge is gone.

- **Set up by Agent Orange the first time an agent opens Blender:** it fetches v1.0.3, puts its
  `blender-mcp` server in a Python environment in the agent folder, and builds and installs its
  extension into Blender with Blender's own command line. Nothing for the person to do.
- **Blender didn't start after the setup:** the setup runs Blender twice, the tool monitor saw those
  runs as Blender being open, and open_tool skipped starting it. It now looks at the running programs
  itself, and if Blender is open without its MCP, tells the agent what the person can do.
- **A real agent made a low-poly planet and moon,** looked up the glTF exporter in Blender's docs
  through the tools, exported planets.glb and checked it held just the two. From its notes: Blender's
  splash covered its screenshot (Blender is now started with the splash off for that start only, and
  the person's preference is put back unsaved); Blender Lab's render tools save in Blender's temporary
  folder whatever path they're given (their design: the briefing now says so); and almost all real
  work goes through execute_blender_code.

## 2026-11-15 · Creative tools used by agents through Agent Orange

The user meant the tools, not Agent Orange's own: Blender, Aseprite, ArmorPaint and Material Maker.
None was usable by an agent: nothing in Agent Orange opened one, an agent's session only connects to
MCP servers already running when it starts, and Blender's was "Port only", which isn't MCP.

- **Installed the three forks** with Settings › Tools' Install buttons. They were all named just
  "Install" (and "Set up"), so the drive tool, and a screen reader, couldn't tell them apart: now
  "Install or update Aseprite" and so on.
- **Agent Orange's MCP server now opens and passes calls to the tools** (`MainWindow.Tools.cs`,
  `McpClient.cs`): list_tools, open_tool (starts it in the project's folder and waits for its MCP),
  tool_tools (what it can do) and use_tool. It talks HTTP (JSON or event streams) and programs over
  stdio, so a tool opened partway through a session is usable straight away.
- **Blender, at first, through a bridge of Agent Orange's own,** which Blender was started with.
  Replaced by Blender Lab's official Blender MCP (above).
- **A real agent used all four:** a green low-poly sphere exported from Blender, an orange circle
  sprite saved from Aseprite, a bricks material exported from Material Maker, and the sphere imported
  into ArmorPaint and captured. The game's folder was untouched.
- From its notes: Blender had no screenshot (Blender Lab's has them); every call goes through
  use_tool, so the tool's own list has to be read first. It also said Material Maker's 3D preview
  came back white: checked, and it isn't a bug. The Bricks pattern is white bricks with black
  mortar, and both the preview render and the 3D panel show them. ArmorPaint saving its capture to
  a folder of its own is how it works, and fine.
- **Approvals couldn't be answered from the keyboard:** Allow, Always allow and Don't allow were
  words with a mouse handler. They're buttons now, looking the same.
- **The MCP relay locked the dev build's own files,** so rebuilding while an agent ran failed and took
  the app down. A dev build now runs the relay from a copy of itself in the agent folder.

## 2026-11-15 · Agent Orange's own MCP server

With the GDD to be deleted, agents have to read the design from Agent Orange, and they only ever saw
the page the person had open. Now every agent session gets Agent Orange's MCP server
(`McpServer.cs`, `MainWindow.Mcp.cs`): `AgentOrange.exe --mcp <project>` relays its JSON-RPC lines to
the running app over a pipe only this Windows account can open (a separate one for the dev build).
Each project gets its own MCP config, so the server is always about the session's project.

- **Tools:** get_project (settings, pages, milestones, the folder's documents), list_cards,
  read_card (a card's document and its tasks, or the overview), write_card (replace or append),
  list_tasks, add_task, update_task, search (documents, card names, tasks), read_picture.
  Nothing delivers an asset.
- What an agent writes shows at once on the page if it's open, unless the person is writing there.
- Checked through the relay with every tool, then with a real Claude Code session in the agents
  window, which answered from read_card, list_cards and list_tasks.
- Found: asking for milestone "Priority 1" matched Priority 10 and 11 too; fixed.
- Seen: an empty reasoning row (an icon with no words) in that session's work log.
- **A real agent then used every tool once** (on a throwaway card and task) and reported what was
  awkward. Fixed from its notes, and confirmed by it on a second run: add_task and update_task reply
  with the whole task as it now stands, so a change can be seen to have happened; ids are shown
  without the project's id in front, and either form is taken; search says the section a hit is
  under and a task's milestone, with the line in quotes; cards are called "story beat", not "beat".
- Left: the pictures from the GDD have no captions, so an agent has to open one to know what it shows.
- The drive tool's `set` now takes `\n` for a line break, as a command is one line.

## 2026-11-15 · Documents instead of boards for design text

The user: "nodes are not good for documents… text is better for a large gdd like this… node graph
is too hard to look at when it gets this complex". Boards stay, for workflows later.

- **Each card and the overview open on a document** (`DocumentPage.cs`): its picture as a banner,
  Markdown as it's typed or pasted, its headings down the side to jump between, and its board a
  click away ("Board"). Kept as Markdown (`documents`, pictures in `document_pictures`), so agents
  read and write the same text, and told to agents when it's open.
- **One Markdown converter** for both ways (`MarkdownText.cs`), shared with the board's paste.
  Links can go to cards (`card:part/id`) and to the project's files (`doc:path`).
- **Every board was written out as its document** the first time it opened, in reading order:
  links followed from where each chain starts (the user's overview columns), the rest row by row,
  frames as headings. Each board was then emptied, with a copy in the agent folder's
  `board-backups`. All 42 converted: the overview, 2 beats, 4 characters, 8 levels, 27 mechanics.
- Cards show their document's word count instead of a node count.
- Assets keep their board for now: their own screen (brief, candidates, delivery, workflow) is next
  after the MCP server.
- Found on the way: a link at the start of a list item was saved with the bullet in its words.
- Not yet: ticking a tick box in a document with a click, and the button bar the nodes had.

## 2026-11-15 · Animal Planet: the first real commit and push through Agent Orange

The new production documents were committed and pushed to the game repo with the agents window's git
actions, their first use on a real repo. What went wrong:

- **The written commit message was about nothing.** It was generated from `git diff HEAD`, which
  leaves out new files, so it guessed, and added "nothing is staged yet… stage them before you
  commit" to the message. Now the changes are staged first and the message is written from the
  staged patch, as T3 Code does. That commit was undone locally (never pushed) and made again.
- **The commit used this PC's personal email.** Agent Orange's commits now use the GitHub account's
  no-reply address when Git isn't set up with one, as a repo can be made public later.
- **The default-branch dialog cut "Abort" off** when its buttons didn't fit. Its footer now wraps,
  with Abort at the left, as T3's does (not yet seen with the dialog open).
- Left as T3 Code has them, for now: on main, the main action offers "Commit, push & PR" and "Push &
  create PR", a pull request from main into main; the Changes row counts no new files (+0 −0); and
  the menu closes if the window loses focus between choosing it and picking from it.

## 2026-11-15 · Animal Planet: tasks people make, and the implementation plan as milestones

Tasks were only assets with a date to deliver them by, so there was no way to add one by hand, and
the GDD's implementation plan sat in a document though it's a list of work. The user: "things like
this should be tasks".

- **Tasks people make** (`tasks`): what's to be done, notes, a day to have it done by, a milestone,
  and a tick. Per stage, like asset tasks, which stay as they were.
- **Milestones** (`milestones`): a name, what it's for, and "done when", holding their tasks, with
  progress. Deleting one keeps its tasks.
- **The Tasks page** shows them by milestone, then other tasks, then assets to deliver; or by date,
  where tasks keep their milestone order and say which milestone they're in. The overview's Tasks
  done tile counts both kinds. Agents on the Tasks page are told every milestone and task.
- **The implementation plan** went in through the forms: Priority 0 to 11 and Release Readiness, 13
  milestones and 134 tasks. Its document is now only the development principles.
- The asset form's date picker is shared by both forms now.
- **Moving tasks and milestones** (asked for next): drag a task within or between milestones, or onto
  "Other tasks", and a milestone by its title; or Alt+Up and Alt+Down on what has the keyboard, which
  carries a task across into the next milestone at either end. The moved task keeps the keyboard.
- **A task can belong to a card** (story beat, character, level or mechanic), picked as an asset's
  owner is, with the same picker, now shared. The card's name shows on the task and opens it.
  Agents are told each task's card, and on a card, its tasks. 78 of the plan's tasks were linked:
  each prototype milestone to its card, or a closer one its words name. Foundations, the vertical
  slice, content, polish and readiness cut across everything, so they weren't.

## 2026-11-15 · Animal Planet: a lean Overview, frames, undo and game settings

The user found the Overview "super bloated": every Overview article of the GDD had been put on it,
unread. Read properly, most of them aren't an overview. The user rearranged the top themselves
(vision, pillars, target experience, starting experience) and that was kept. The rest went:

- **Open Decision Register:** onto the cards it's about, as tick-box lists under each picture
  (Multiplayer and Visitors, Farming, the wildlife, the residents, The Universe, the Ancient Civilization).
- **Consolidated Decision Register and Version 2.5 Status:** dropped, as they repeat the cards.
- **Story:** two beats on the Story page.
- **Production, release scope, risks and the implementation plan:** Markdown documents in the game's
  folder (`docs/production/`), shown on the Overview as Document nodes in a "Production" frame. They're
  long reference text, read rather than laid out, and agents working in the repo read them directly.
- **Technical targets** such as the frame rate: the user said these "belong as game settings". Project
  settings now has a Game settings section (platforms, frame rate, resolution, minimum hardware,
  online, players, input, languages, session length), synced, and told to agents with every message.

Built for it, as the user asked or the work needed:

- **Undo and redo** on boards: Ctrl+Z, and Ctrl+Shift+Z or Ctrl+Y, 30 steps per board.
- **Frames** to group nodes, as in Blender: Ctrl+J around what's selected, or Shift+A › Frame. What's
  inside a frame moves with it.
- **Document node** (Shift+A › Document): one of the project's files, shown by its title and headings,
  opened with a double-click.
- Drive tool: `select` takes several names, and `selectat X Y` picks by place.

## 2026-11-15 · Animal Planet: the GDD again, the right one, done properly

The first import (below) used the wrong GDD: v2.5 on `main`. The current one is a restructured GDD
with 44 pieces of concept art, published as a page and "structured for import as Overview, Levels,
Mechanics and Characters cards". Its branch was never pushed, so it came from the page. The first
import was also lazy: one text dump per section, no pictures, nothing laid out. It was all deleted,
except the user's own Player card and Player reference node, and done again:

- **A card per article** on Levels (8), Mechanics (27) and Characters (4: the user's Player card took
  Player Character Customization). Each card's picture is its concept art.
- **Each card's board:** the concept art as an Image node on the left, and the article split at its
  `###` sub-topics, one Rich Text node each, in columns beside it. A sub-topic over 45 lines splits
  at its `####` headings.
- **Overview board:** the title and key art at the top beside the Player reference, then a row per
  article: a Title node, its picture if it has one, and its sub-topics in up to four columns.
- **Story:** empty. The GDD keeps its story on the Overview.

What went wrong, and the fix:

- **Pasting a picture had the same real-mouse bug** as adding a node: it went where the mouse was,
  even with the window covered. It now needs the mouse over the board itself.
- **The drive tool couldn't answer dialogs** in the live app. A card's delete confirmation and picture
  picker would have opened real dialogs on the person's screen. `answer "..."` now answers the next
  one, and the real dialog opens only when nothing was queued.
- New for driving: `point X Y` (where a node added next goes, as the mouse would say), `move X Y` and
  `size W` (the selected node, as dragging would), `nodes` (each node's place, size and name).
- **Opening a project sometimes waited over a minute** on "Checking the repo…". A timed run took 7 s,
  4 of them GitHub's access check. Not reproduced: issue #91.

## 2026-11-15 · Animal Planet: bringing the GDD in (the wrong one)

Animal Planet became the Unreal Engine game (it was the web game). Its design was a 2,500-line Markdown
GDD in the repo (`docs/GDD.md`, v2.5). It was brought in by hand, through the app, without an agent:
one card per topic and one Rich Text node per GDD section, every one of the 105 sections placed once.

- **Overview board:** the document's header and conventions, sections 1 to 3, then a node for each of
  Parts IX (69 to 75) to XIV and the version status. The Player reference node already there was kept.
- **Story (6 beats):** Starting Experience, The Ancient Civilization, Story Delivery, Puzzles, Narrative
  Journal and Artifacts, First Public Release Narrative.
- **Characters (3):** Player (the card that was there), Talking Animal Residents, Original Fantasy Wildlife.
- **Levels (7):** Universe Structure, Spherical Planet Design, Wilderness Planets, Home Biomes, Temperate
  Forest, Desert, Tropical Island.
- **Mechanics (20):** from Spherical Movement and Camera to Online Play and Offline Simulation.
- **Assets:** none. The GDD names no specific assets yet.

What went wrong, and the fix:

- **An agent couldn't do it.** Asked to bring the GDD in, it said it had no way to write to Agent Orange's
  pages. Agent Orange's own MCP server is still to build.
- **Pasted Markdown stayed raw.** A Rich Text node only turned Markdown into formatting as it was typed.
  Pasting now converts it: headings, bullet and numbered lists, tick boxes, tables, bold, italic,
  struck, code, links, and two-space line breaks (`NodeCanvas.Markdown.cs`). A long paste or a table
  widens a new node to 560 px.
- **Escape threw away a Rich Text node's edit,** a whole pasted section with it. Escape now finishes and
  keeps the words, as in Miro and Figma. A node's one-line name still takes Escape as cancel.
- **The text bar sat far from its node,** at the bottom left. It measured itself with its old margin
  counted in.
- **Nodes added from the keyboard landed on top of each other** in the middle of the board. They now
  go in free space to the right, brought into view.
- **The real mouse decided where a node went,** even with the window covered, as only the board's
  bounds were checked. It now has to be over the board itself.
- **No way to see a whole board.** Home frames every node, as in Blender, from the top left when
  they're too big to fit.
- **Keys went nowhere after the add list closed** without a choice. Focus now goes back to the board.
- **The project's folder showed only the start of its path,** the same for every project. It now shows the end.
- **The name box in "New mechanic" and the like** sat out of line with its label and button.
- Nodes have names for screen readers: a title, a Rich Text node's first line, or the card it stands for.

For driving the dev build (`tools/drive.ps1`):

- It failed in Windows PowerShell 5.1, which has no `PipeOptions.CurrentUserOnly`. The app's end of the
  pipe still only lets this Windows account in.
- New: `key Shift+A`, `key Ctrl+V` (shortcuts, with Ctrl+V/C/X/A/Z/Y run as the keyboard would),
  `pick "Web" "Unreal Engine"` (drop-downs), `focus "Board"` (by name), `select "Name"` (a node on the
  open board), `focused` (where keys go), bare `wait` (half a second). Failures say where in the app.
- Hot reload sometimes misses a changed method, and never runs new static fields' initialisers.
  Restart `dotnet watch` when a change doesn't show.

## Unreal 5.9 through Agent Orange, with Unreal's own skills

Unreal 5.9 ships agent skills (`Engine/Extras/AgentTools/Skills`, the web platform's, PCG's). Agent
Orange now gives them to an Unreal game's sessions, and notes every skill an agent uses (Settings ›
Skills). A real agent then made a test level in Animal Planet through Agent Orange: a floor, a light, a
cube, sphere and cone, saved as `Content/Test/AgentTest.umap` (left untracked, not committed). It
checked the level with Unreal's editor screenshots, fixed a black sky and unlit sides with a sky
atmosphere, sky light and fog, and checked the log with Unreal's `unreal-log-triage` skill, which it
loaded by itself. Agent Orange noted the use.

- **A call failed with "MCP doesn't answer"** just after Unreal opened, though list_tools had said it
  answered: the editor was busy and missed the three-second look. A call now gives an open editor half
  a minute before saying so.
- **A skill call showed only as "Skill"** in the conversation, and a passed-on call only as
  "agent-orange: use_tool". They now name the skill, and the tool and its tool, such as
  "Unreal Engine: call_tool".
- **Settings › Skills didn't show a new use** when Settings was opened again on that page, as it kept the
  page drawn before. The page is drawn again each time Settings opens.
- **The agent said Unreal has no tool to save a level, and saved through Python. Wrong:** a level is an
  asset, and the Asset toolset's `SaveAsset` saves it (the Level toolset's `LoadLevel` can also save
  first). It only looked in the Level toolset, which has `SaveActor` and nothing named for the level.
  The briefing now says so.
- Unreal's side, not Agent Orange's: about 80 toolsets in one list, tool schemas of up to 25 KB, and
  Python wanting `read_me` first.


## Building Animal Planet: planning the first slice (2026-11-15)

The design was read through the app (Overview, Player, Movement and Camera, Player-Owned Home Planets,
Tasks), then an agent in the agents window planned slice 1, a placeholder character walking round a
small sphere, with the plan-the-next-slice skill. It read the design with get_project, list_tasks and
read_card, and brought back seven questions for the user before building.

- **The drive tool couldn't read a document or an agent's reply.** `texts` lists only TextBlocks, and
  documents and replies are rich text boxes, so reading the design or a plan took screenshots page by
  page. New `read` command: the whole text of each visible document editor or reply.
- An agent's reply's "Copy message" shows only under the mouse, so the drive tool can't click it.
  `read` covers what it was wanted for.
- **Settings › Providers didn't say which account a login is,** only "Claude Pro subscription", so there
  was no telling whether the agent ran on the right account. A Claude login now shows its email too.
- **An empty reasoning row:** newer models send their thinking with no words, only that it happened,
  and the row showed a brain icon and nothing else. It now says "Thinking", then "Thought".
- **Reading a skill asked for approval.** The briefing points agents at each skill's SKILL.md, in
  Agent Orange's folder or the engine's, and every read of one stopped the agent until it was allowed.
- A message sent while the agent works goes in as steering, which is what was wanted: the multiplayer
  rule reached it mid-turn.
  Sessions now get a settings file (`--settings`) allowing Read of every skill's folder.
- **Approving every small command wasted the person's time and tokens.** The agents window now offers
  Claude Code's **Auto** mode (it judges what's safe and asks only before something risky), and it's the
  default for new conversations. For Codex, Auto never asks and stays in the project's folder.
- Adding a mode or a field restarts `dotnet watch`, which closes Unreal and cuts the agent's turn short.
  A short "carry on" message after the restart picked the work back up.

## Slice 1, tasks 2 and 3: the planet and the placeholder (2026-11-16)

Fable 5.2 (Extra High, Auto) built a 172 m whitebox planet with true sphere collision, landmarks and a
start flag, and a replicated orange capsule character, then checked both with editor screenshots and a
2-player Listen Server run, which caught the sky throwing both players 10 km up. It stopped with
questions about the DualShock 4 (Unreal's free Game Input plugin needs Microsoft's runtime installed),
shadows and the look.

- **Its screenshots came as a list of file names,** so seeing them meant opening a folder. The briefing
  now tells agents to put pictures in replies as Markdown images, which the agents window draws.
- **Codex's rows didn't say which account they were either.** They now show the email from the ID token
  in Codex's own login folder (only that claim is read).
- **The tray-hidden window stopped the drive tool:** after a restart the app came up in the tray, and
  every command found nothing. New `show` command, as clicking the tray icon.
- **Two dev builds ran at once,** from an old `dotnet watch` left over from before the app was closed.
  Stopped it; only one `dotnet watch` should run.
- **Codex in Auto refused its own tools.** Auto mapped to "never ask", which Codex takes as "turn down
  anything that would need asking", so it stopped before it could read anything. Auto now works as Edit
  freely does for Codex. (The user then chose Claude, not Codex, for coding: Opus for code, Fable for
  visual work, High effort at most.)
- **A Claude conversation can't move to Codex,** so going to Codex meant a new conversation and a
  hand-written brief.
- **A usage limit stopped the agent mid-task** (nate's 5-hour limit, after 34 minutes of Opus). "Go on
  with Claude (lc)" carried the conversation to the other login and went on, as it should. Its note
  said "Sent for you once the usage limit reset", which wasn't what happened; it now says "Sent for
  you, to go on after a usage limit".
- **A finished turn still showed "Working".** A message sent while the agent works (the Game Input
  approval) was taken into the turn it was on, and Claude Code then finished once, not once a message.
  Agent Orange counted two messages and waited for a second finish that never came, so the agent had
  stopped with a question while the list said "Working 15m". Now, when nothing at all comes within 20
  seconds of a finish, nothing is waiting and it's done.
- **The clip button's file picker ignored the drive tool's `answer`,** as it opened its own dialog
  rather than going through `Dialogs`, so attaching a playtest screenshot took a paste instead. It now
  goes through `Dialogs.PickFiles`.
- **Playtest feedback, through Agent Orange:** the user's first playtest found the camera looking
  straight down on the head by the start flag (the obstacle rule rising for a thin pole). Design call:
  small props never move the camera and fade when in the way; only big ones make it rise, to 60° at
  most.
- **Changing model in a conversation re-reads all of it,** which the user called a waste of tokens:
  the conversation was at 72% of its context when it moved from Opus to Fable. The rule now is a new
  conversation with a short brief for each change of model. Agent Orange could help here: offer "New
  conversation from here" with a summary when another model is picked in a long conversation.

## Slice 1 committed and pushed through Agent Orange's Git panel (2026-11-16)

The first real use of the agents window's Git actions on the game repo: Commit (message written by
the login: a good summary of the slice), then Push to main past the default-branch warning.
`e784a65` is on GitHub.

- **The Git options menu closed as soon as it opened.** Opening it asks for the repo's state, and the
  answer redrew the panel, which removed the menu. A person would have seen it flash and vanish. The
  panel is no longer drawn again under an open menu, nor when the state hasn't changed.
- **`click "Commit"` in the drive tool** most likely went on to the commit dialog's own Commit button, so
  the file list was never seen and a throwaway test level went in with the slice. Drive ambiguous names
  one step at a time, and check the dialog before confirming.
- **`dotnet watch` stopped after two hours,** taking the app and Unreal with it: Claude Code ends
  background commands at two hours. It now runs in its own console window (see the handoff).
- **No Git LFS in the game repo.** "Create the Unreal project" wrote a .gitignore but no .gitattributes,
  so the first slice's assets, maps and ~15 MB of screenshots went into plain Git. It now also writes a
  .gitattributes sending Unreal assets, art, models, sound, video and fonts through LFS, and ignores
  plugins' Binaries and Intermediate. An agent set up LFS in Animal Planet's repo, converting the
  committed files in a new commit (no history rewrite, so no force push) and removing the test level.
- **A conversation looked finished while the agent's background job ran.** The agent started a 40-minute
  browser measurement in the background and ended its turn ("the job will notify me when it ends"); the
  list said "Worked for 1h 12m" and nothing showed a job still running or that the agent would come
  back by itself when it ended, so the user thought it was stuck. To build: show running background
  jobs on the conversation ("Waiting on 1 background job") until Claude Code says they've ended.
  **Built:** a command an agent starts with run_in_background counts as a job; when Claude Code starts a
  turn by itself (a job ended), one is counted as ended and the conversation shows Working again. While
  jobs are left, the list says "Waiting on job" and the timeline "Waiting on a background job. It carries
  on by itself when the job ends." Tried with a 60-second job: it waited, came back by itself, and cleared.
- **The supervisor's own watching went wrong three times:** a watcher keyed on the "Working" label, a
  leftover `grep` whose command line looked like a build, and a 10-minute quiet window all hid a finished
  agent. And a brief with double quotes inside it was cut short by the drive tool's `set`. With "Waiting
  on job" in Agent Orange, the label now tells the truth.

## Priority 2, the spaceflight prototype (2026-11-16)

Opus 5.5 built and checked the whole gate: walk on planet A, board your own ship, fly to planet B in the
same world (about 40 s), land in a clear spot and walk on, with gravity from whichever planet is near,
2 players, no loading. Also landing checks, recovery, summoning, and a warp to a second test system with
ship and player state through a stand-in backend (JSON). One web build at the end found two control bugs
the editor tests missed; both fixed.

- **Agent Orange's tools to Unreal returned 404 and the agent went around them.** Agent Orange keeps one
  connection per tool and reused Unreal's session; the agent restarted the editor during the work, and a
  new editor answers an old session with 404 (as MCP says it should). Agent Orange now starts a new
  session and sends the call again, once.
- **The agent didn't say so plainly,** and while looking for another way in it read a login folder's
  .claude.json. The briefing now says to report a failing Agent Orange tool with its error rather than
  work around it, and never to read Claude Code's or Codex's login folders.

## Planning Priority 5 (farming) with Codex (2026-11-16)

The model baseline puts planning on Codex GPT-6.1-Sol, so this was the first real Codex work in a while.

- **Codex couldn't use any of Agent Orange's tools:** every call came back "user rejected MCP tool
  call". Codex now asks the app to approve each MCP tool call as an MCP elicitation with nothing to fill
  in, and Agent Orange turned every elicitation down, as T3 does. Codex only gets Agent Orange's own MCP
  servers, which Claude sessions use without asking, so an approval-only elicitation is now accepted;
  real forms are still turned down. The agent said so plainly, as the new briefing line asks.
- **Codex in Auto still asks before reading a skill's SKILL.md** with a shell command. Allowed for the
  conversation by hand for now; skills for Codex are #89.
- **Agents couldn't add cards (#92),** so a slice plan's assets couldn't reach the Assets page. New
  add_card tool: a story beat, character, level, mechanic or asset, as the page's Add form does, with
  its document, and for an asset its type, the card it belongs to and a deliver-by day. Agents still
  never fill a deliverable.
- **Agents couldn't see a card's own picture,** the concept art on Characters, Levels and Mechanics cards:
  read_card gave only the document, so the planner judged the style from the Overview's four pictures.
  read_card now names the card's picture (picture:card-id), read_picture opens it, and list_cards says
  which cards have one.
- add_card worked: the planner put 20 farming assets on the Assets page, each with its type and owner
  card. But it named them all "Farming — ...", so every tile shows the same F and the names are cut off
  before the part that matters. Worth showing an asset's type icon when it has no picture.
- **"Waiting on job" was wrong for a service.** The farming agent started its local backend in the
  background so the game could save, finished its turn, and the list said "Waiting on job" for half an
  hour: the job was a server meant to keep running, not something it waited for. Only that a command is
  still running is known, so that's all it says now: the list shows Done as usual, and under the
  timeline "1 command it started is still running in the background (command). It goes on by itself if
  it was waiting for one."
- **The drive tool garbled Japanese.** The agent's report on the farming screens' Japanese came back from
  `tools/drive.ps1 read` as mojibake, though AO's own window drew it fine. Windows PowerShell wrote the
  reply in the console's code page; it now writes UTF-8, so Japanese and curly quotes come through.
- **The drive tool couldn't see an agent's pictures.** The crop agent showed its render and an in-game
  screenshot as pictures in its reply, but `read` printed only the words, and the drive can only scroll a
  conversation to its top or end, so the pictures had to be found in the game's folder instead. `read`
  now ends each document with a "Picture: path" line per picture (needs an app restart: the drive's
  command loop is already running, so hot reload can't swap it).
- **Agents can't write to a milestone.** Finishing Priority 5's browser check, the agent wanted to put the
  evidence on the milestone and found no field for it: the MCP tools read a milestone's goal and "done
  when" but can't change them, and milestones have no notes. It added a ticked "Priority 5 browser
  check" task instead, which reads fine on the tasks page. Not built: an `update_milestone` tool, if
  agents keep needing one.

## Planning Priority 6 (residents) with Codex (2026-11-16)

- **Codex stopped again to ask before reading a skill's SKILL.md,** in Auto. Agent Orange now allows it
  itself in Auto: a command that is one plain read (Get-Content, cat or type, maybe inside
  `powershell -Command`) of a file inside a folder of a skill the session was given, with nothing chained
  or substituted. Anything else still asks.
- **Agents couldn't order tasks.** The planner put "P6 00" to "P6 14" in the titles ("titles encode order
  because task tools expose no reorder field"), and the tasks page still listed them out of order.
  `update_task` now takes a position among its milestone's tasks.
- **Shared rules went into every task's notes.** The same long paragraph of constraints opens all of
  Priority 6's tasks, as a milestone has nowhere to keep them, so the tasks page, which shows a note's
  first lines, shows the same words under every task. Second sign that milestones want notes agents can
  write (see "Agents can't write to a milestone").
- **The drive tool couldn't read a task's notes:** they're in an edit box, which `read` skipped. It now
  prints boxes of more than one line too.
- **AO had no way to control the repo outside the agents window.** Committing, pushing, pulling or
  opening a pull request meant opening a conversation and its details panel. The project's page now has
  the same Git actions group beside "Open in Unreal Engine": the action that fits, the menu with its
  reasons, the commit dialog, the default-branch warning and the toasts, acting on the project's folder.
  With no conversation, commit messages and PR text are written by the login a new conversation would
  start with. Both windows draw it from one shared copy (`GitActions`), so an action run in either shows
  in the other.
- **"Sync ref" did nothing.** On the user's other PC the project page said "Branch has diverged from
  upstream. Rebase/merge first.": that PC had commits origin didn't, and origin had the agents' pushes.
  T3 leaves that to the person; Agent Orange's person may never use Git, so Sync ref now does it: it
  puts this PC's commits after origin's (`git pull --rebase`) and pushes. When both sides change the same
  files it puts everything back as it was and names them.
- **A menu could float on over every window.** The project page's Git menu stayed on screen after
  leaving the page, and even over the agents window: a pop-up is a window of its own, and its button
  had been hidden. Every T3-style menu (`Ocean.Menu`) now closes when the button it opened from is
  hidden or taken away.
- **A service an agent starts dies after two hours.** The slice B agent ran the game's local backend as
  a Claude Code background command, and Claude Code stops those at two hours, so the backend went
  down mid-session (it said so, and starts it again when needed). Not built yet: services a project
  needs while agents work (a game's backend, a web server) could be kept by Agent Orange itself, like
  the editors, instead of living inside one agent's session.
- **The drive tool can only read the conversation's newest replies.** The list is virtualised, so
  `read` missed slice B's long summary once a shorter reply followed it; the review file in the repo
  had it all.
- **Agents couldn't tag assets.** The user found the Assets page hard to see and sort: the farming agent
  had added 20 assets with no tags, because `add_card` had no tags and nothing could change an asset's
  details afterwards. `add_card` now takes tags (by name, made when new), a new `update_asset` changes an
  asset's type, owner, tags or date (never its deliverable), and `list_cards` shows each asset's tags
  and the project's tags, so agents reuse them.
- **Codex in Auto asked before reading the project's own files** (a `Get-Content` of five design docs,
  quoted in single quotes). The Auto rule now covers plain reads of files in the project's folder
  (not `.git`) as well as skills', several at once, relative to the project.
- **First real commit and push from the project page** worked: 30 GDD images an agent had left outside
  LFS (they were committed before `.jpg` went to LFS) were committed with a written message and pushed
  to main, with the default-branch warning on the way. But for a project that always pushes straight to
  main, the main button keeps offering "Commit, push & PR" / "Push & create PR", so every push goes
  through the menu and the warning. Not built: a project setting for "no pull requests here" that
  makes the main action "Commit & push" and skips the warning.
- **Adding tasks by hand from the Tasks page worked** (three designer tasks: camera, orbits, space look).
  Two small things: the New task form's fields have no names for screen readers or the drive tool
  (only their place in the form finds them), and the form opens partly outside the window's right
  edge on a 1084 px wide window.
- **Agents couldn't remove a card.** Splitting the asset "sets" into one card per file left 61 old cards
  for the person to delete one at a time (right-click, Delete, confirm). A new `remove_card` tool deletes
  a card as the page's Delete does, by its whole name or id only (never the start of a name), and won't
  delete a delivered asset or a card that assets still belong to.
- **"A command is still running in the background" stayed after it had ended.** Agent Orange counted a
  background command as ended only when Claude Code started a turn by itself for it, so one that ended
  while the agent was still working was never counted. Claude Code says when each starts and ends
  (`task_started`, `task_notification` with the command's tool id), and those are read now; the old guess
  is kept only for a session that doesn't say.
- **A Codex conversation showed Working for almost four hours after its last answer.** The asset split's
  final reply came at about 02:15 and Codex never said the turn had completed; Stop ended it. Nothing
  says why yet, so Agent Orange now logs (once each) any Codex event it doesn't read, to leave a trace next
  time.
- **drive `shot` with no name failed** (index out of range), and a name ending in `.png` got a second
  `.png`. It names the picture by the time now, and keeps a given `.png`.

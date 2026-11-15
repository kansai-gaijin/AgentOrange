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

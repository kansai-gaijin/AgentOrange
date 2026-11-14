# Dogfooding log

What got in the way while using Agent Orange to make the games, and what was done about it. Newest first.
Anything not fixed on the spot is a GitHub issue, not a line here.

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

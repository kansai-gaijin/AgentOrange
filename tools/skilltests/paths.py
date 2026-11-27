"""Where the skill tests' code, data and tools are, on any machine.

The code lives in the repo (this folder). Everything a run makes, and the inputs it needs that don't
belong in the repo, live in a data folder outside it:
    SKILLTESTS_DATA (default D:\\blenderskills)
        sktest\\   fx (practice files), refs*, run (one folder per run, logs), results, outpage, page
        skdata\\   a copy of an Agent Orange agent folder: agent\\providers\\claude\\<id>\\agent-orange
                   (the plugin with the skills the runs load) and agent\\briefings
BLENDER is Blender's executable (default: the Steam install).
"""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SKILLS = os.path.join(REPO, "client", "Skills")
DATA = os.environ.get("SKILLTESTS_DATA", r"D:\blenderskills")
WORK = os.path.join(DATA, "sktest")
SKDATA = os.path.join(DATA, "skdata")
BLENDER = os.environ.get("BLENDER", r"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe")

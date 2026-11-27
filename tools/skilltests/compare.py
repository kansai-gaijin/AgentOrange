"""The person's own grades beside the judge's: python compare.py (reads mine/mygrades and results in the data folder)."""
import glob, json, os, sys
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from paths import WORK  # noqa: E402
W = {'A': 'Great', 'B': 'Good', 'C': 'OK', 'D': 'Poor', 'F': 'Bad'}
P = {'A': 4, 'B': 3, 'C': 2, 'D': 1, 'F': 0}
for f in sorted(glob.glob(os.path.join(WORK, 'mine', 'mygrades', '*.json'))):
    d = json.load(open(f, encoding='utf-8'))
    d = d.get('data', d)
    s = os.path.basename(f)[:-5]
    j = json.load(open(os.path.join(WORK, 'results', s + '.json'), encoding='utf-8'))['grade'].get('grade', '?')
    m = d.get('grade', '')
    diff = P.get(m[:1], -9) - P.get(j[:1], -9) if m else None
    notes = d.get('notes', '').replace('\n', ' / ')
    print(f"{s:30} me={W.get(m[:1], '-'):5} judge={j:3} d={diff} | {notes}")

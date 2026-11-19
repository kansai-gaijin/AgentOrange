# Grades in ffmpeg

From AnyShot's `agent/SHOTS.md` ("Grading") and AnyScene's `grade.sh` (MIT), with NVENC swapped for libx264.

Grade in ffmpeg, not Blender's compositor: in 5.0 its colour-balance, mask and ellipse nodes misbehave (Glare and
Lens Distortion are fine, and camrig's `lens_fx` uses those). Use one grade on every shot of a set.

## The looks
- **clean:** a light `curves` contrast and nothing else.
- **cinematic** (the default): curves lifting the blacks to about 0.03 with teal shadows and warm highlights,
  `hue=s=0.88`, a slight `rgbashift`, a soft `vignette`, and a little temporal `noise` for video.
- **gritty:** more contrast, `hue=s=0.75`, stronger grain and vignette.
- **vintage:** faded blacks, a warm cast, `hue=s=0.7`, grain, a gentle glow.

The cinematic chain, exactly as AnyScene used it:
```
GRADE="curves=r='0/0.03 0.25/0.23 0.6/0.635 1/0.985':g='0/0.034 0.25/0.245 0.6/0.615 1/0.965':b='0/0.05 0.25/0.27 0.6/0.6 1/0.93',hue=s=0.88,rgbashift=rh=-1:bh=1:edge=smear,vignette=angle=PI/4.4"
```

## A still
```
ffmpeg -v error -y -i still_0001.png -vf "$GRADE,noise=alls=5:allf=u" -frames:v 1 still_graded.png
```

## A clip: check the frames, then grade and encode in one pass
A gap in the frame folder silently gives a short clip, so count first:
```
n=$(ls shots/level_walk/frames/*.png | wc -l)      # must equal the shot's "frames"
ffmpeg -v error -y -framerate 24 -start_number 1 -i shots/level_walk/frames/%04d.png \
  -vf "$GRADE,noise=alls=7:allf=t+u,format=yuv420p" \
  -c:v libx264 -preset slow -crf 18 -movflags +faststart level_walk.mp4
ffprobe -v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 level_walk.mp4
```
The last line prints the frame count of the result; it must match `n`. Without ffprobe,
`ffmpeg -i level_walk.mp4 -map 0:v:0 -c copy -f null -` ends with the same count as `frame=`. In PowerShell, count with
`(Get-ChildItem shots/level_walk/frames/*.png).Count` and write the filter in single quotes.

For a store-page video, keep the cinematic grade gentle (or use clean): stores show it small, and crushed shadows
hide the game.

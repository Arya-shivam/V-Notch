# Review — V-Notch launch film

## Direction
Selected “The notch as a switch.” Three short problem prompts lead to supported proof: provider-by-provider usage, PC stats buried in Settings, and hard-to-find drive space. The cut ends on the genuine product showcase and the GitHub Releases page. Other storylines not built: “One notch, many signals” and “The notch goes to work.” New-widget UI is unavailable, so no dashboard or sample metrics are fabricated.

## Evidence
- HyperFrames check: zero errors/warnings across lint, runtime, layout, craft and audio; contrast 13/13. Duration 23.1 s / 693 frames.
- `snapshot --review`: 47 frames, 12 beats and 7 seams; inspected beat, phone, product-to-endcard and final-card sheets. Rendered MP4 frames checked at 3.9, 5.2, 7.6, 9.1, 11.2, 14.9 and 19.75 s.
- `timeline --json`: six scene hosts and three timed audio clips. The music bed is trimmed from 6.9 s; its rising accent lands at the 16.4 s wipe. The end card and release URL hold through the final frame.
- Bundled SFX measured: whoosh 0.575 s, peak at 0.162 s; starts 1.871 s and peaks at 2.033 s, one frame after the 2.0 s iris. Bass impact 2.592 s, peak at 1.986 s; starts 11.047 s and peaks at 13.033 s, one frame after the 13.0 s product reveal. Both use 0.35 gain.
- Final render: `renders/v-notch-launch-cinematic.mp4`, H.264/AAC, 1920×1080 at 30 fps, 23.1 s, 5.9 MiB. Audio measured at −16.1 LUFS integrated, −12.9 LUFS short-term max, −1.7 dBTP; zero clipping, no analyzer warnings. Signal measured, not heard.
- HyperFrames desktop preview loaded at `http://localhost:3003/#project/launch-film`; final render completed.

## Weakest shot and next change
System and storage proof is deliberately typographic: it names the readings and sampling/source behavior but shows no live values. Replace it with genuine app footage if the new widgets can be captured; do not add invented readings.

## Sound status
The local MusicGen bed is 128.4 BPM, trimmed to start at 6.9 s so its rising accent lands on the end-card wipe. The final mix measures −16.1 LUFS integrated and −1.7 dBTP with zero clipping. No audio was heard in this environment.

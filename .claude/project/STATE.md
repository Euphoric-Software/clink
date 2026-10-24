# State

## In progress
Manual start/stop recording (branch `feature/manual-recording`).
Step 1 done: progressive Mp4Muxer + journal/Recover (commit 3855739).
Verified: Debug build 0 warnings; `--unittest` 321 passed/0 failed/0 skipped (also with CLINK_TESTS_NO_SKIP=1 and CLINK_EMULATE_ELST=1).
Step 2 done: incremental ClipMuxWriter (commit b9591c5); ClipWriter.Write is a wrapper, clips byte-identical (golden SHA-256 tests). Verified: build 0 warnings; 330 passed/0 failed/0 skipped.

## ClipMuxWriter API
ctor(ClipOutput FastStart(path) | Progressive(inProgress, final), ClipVideoConfig(Codec, Width, Height, Fps, SequenceHeader?), IReadOnlyList<AudioTrackDesc>, origin100ns, aligned). AddVideo(pts, key, ReadOnlySpan<byte> annexB); AddAudio(track, in AacFrame); NextVideoPts; Flush(durable); ClipMuxResult Finish() (Path, Duration100ns ~ video span, SizeBytes, AudioTracks, VideoSamples, AudioSamples[]); Discard(); Dispose() keeps .rec/.idx. Properties: HasVideo, VideoElapsed100ns, DurationLimit100ns. Single-thread. No monotonic-pts guard: the sink owns it. Progressive mode creates no files until parameter sets are known.

## Key decisions
- moov-at-end + sidecar journal chosen over fragmented MP4: keeps box layout identical to clips so Trimmer/ClipSource/ClipAlignment/clk1 brand work unchanged; fMP4 would risk MF edit-list and seeking behaviour.
- Recording shares the replay encoder (quality = replay quality).
- Recording starts from the ring's last keyframe at or before the hotkey (<=1 GOP pre-roll), no forced IDR.
- Engine restarts (settings change, display change, sleep, pause) end the current part; controller starts a new part.
- Recordings land in `<ClipsFolder>\Recordings\`; in-progress files use `.mp4.rec` in the final directory.

## Sink contract (Mp4Muxer progressive mode)
- Single owner thread for Write*/Flush/Finish/Discard/Dispose.
- Flush(false) every 1-2 s, Flush(true) every 10-30 s (FlushFileBuffers stalls).
- Know avcC before CreateProgressive (SPS/resolution change => new file).
- Stop before DurationLimit() with a margin using VideoElapsed100ns, or catch DurationLimitException then Finish.
- Any other Write* exception => stop writing, Dispose, Recover.
- Finish throws => do not retry on the instance; Dispose then Recover.
- "Nothing recorded" => Discard.
- Startup recovery: Recover each *.rec with .idx not currently live (IOException => in use, skip; null => keep files, don't delete); delete orphan .idx without .rec; delete header-only .rec without .idx.
- Ensure the final directory exists; read FinalPath after Finish.

## Known limits
- Index memory ~20-40 MB/h (cap parts at ~4 h).
- 32-bit duration limit ~13.25 h.
- Trimmer loads whole selections in memory (unsuitable for hours-long files until seek-and-stream).

## Next action
Step 3: RecordingSink + Engine API (see TASKS.md).

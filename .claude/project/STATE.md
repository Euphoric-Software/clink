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
- Flush(false) every 1 s, Flush(true) every 5 s (contract updated from 10-30 s to 5 s).
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

Step 3 done (commit 73abefc): RecordingSink + Engine recording API + SelfTest --record. Verified: build 0 warnings; 360 passed/0 failed/0 skipped (also CLINK_TESTS_NO_SKIP=1); real `--selftest 10 --record` exit 0, 10.47 s HEVC + 5 AAC tracks, dropped video frames 0, clip saved mid-recording OK.

## Engine recording API (Engine.cs)
- `public bool IsRecording`
- `public RecordingStatus? Recording`
- `public event Action<RecordingResult>? RecordingStopped`
- `public string StartRecording(RecordingOptions options, string? gameHint = null)`
- `public Task<RecordingResult> StopRecordingAsync()`
- `public static IReadOnlyList<RecordingResult> RecoverRecordings(string folder)`

## Sink behaviour (RecordingSink)
- Writer thread "Clink.Recorder"; encoder/audio threads only copy into pooled buffers and enqueue. Starts at ring's last keyframe via shared gate.
- Byte budget max(64 MB, 15 s at bitrate); drop-to-keyframe backpressure; Overflow stop after 30 s of sustained overflow.
- Discontinuity if gap >5 s. MaxDuration default 4 h, also limit-60 s of the 32-bit mux duration limit.
- DiskFull when free space <1 GB, checked every 5 s.
- Flush(false) 1 s, Flush(true) 5 s.
- Clean stop: <=800 ms audio catch-up, audio cut at endPts for every track.
- Taps detached before teardown; Dispose waits <=15 s for an in-flight finish.
- Recovery never deletes files it cannot positively identify.

## Residual risks
- Hung disk I/O cannot be interrupted (Dispose waits <=15 s then proceeds).
- L3 untested (no seam).
- H1 shutdown race covered by code reading only.
- Real-file recovery not yet exercised; leftover killed pairs for a manual test are in C:\Users\Jason\AppData\Local\Temp\claude\C--Users-Jason--claude-spares-spare-RBufNV\181a9c26-3628-401c-b06d-642afbeb070d\scratchpad\rec5 and ...\scratchpad\rec7_1500.
## Next action
Step 4: AppController integration (see TASKS.md, incl. review requirements).

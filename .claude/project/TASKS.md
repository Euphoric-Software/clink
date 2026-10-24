# Tasks

- [x] Step 1: Mp4Muxer progressive mode + journal + Recover + tests
- [ ] Step 2: Engine/ClipWriter.cs -> incremental writer (AddVideo/AddAudio/Finish), Write stays a wrapper; existing tests green
- [ ] Step 3: Engine/RecordingSink.cs (queue + "Clink.Recorder" writer thread, byte budget max(64 MB, 15 s), drop-to-keyframe backpressure, limits, stop with audio catch-up <=800 ms), Engine API (StartRecording/StopRecordingAsync/IsRecording/Recording status/RecordingStopped event/RecoverRecordings), _tapGate fan-out at Engine.cs:172, AudioTrack.FrameTap, detach taps before TearDown, Dispose waits <=15 s; SelfTest --record; tests with fake producers
- [ ] Step 4: AppController toggle ("record" hotkey action in HotkeyListEditor s_actions + OnHotkey switch), RecordingStopped marshalled to UI -> Library.Add + toast, postpone settings restarts while recording, auto new part after unavoidable restarts, startup recovery, SessionEnding finish, StatusDot recording state (distinct red + timer), tray item
- [ ] Step 5: Settings (recordings folder, max part length), ClipLibrary scan of Recordings subfolder + IsRecording badge, archive opt-in decision, Trimmer seek-and-stream or selection cap, SettingsImport categories
- [ ] Manual soak: 2 h recording, clip during recording, kill process -> recovered on restart, display change -> part 2

namespace Clink.Services;

// What the remote status pipe (RemoteServer) tells a client about Clink. Immutable, with value equality, so the server can tell "nothing
// changed" from "send again". Booleans, whole numbers and fixed codes only: no game name, account name, link, token, path or error text.

/// <summary>The manual recording: going (<paramref name="On"/>), its whole seconds, between two parts (<paramref name="Resuming"/>), asked for but waiting for the recorder (<paramref name="Queued"/>), its file being finished.</summary>
internal sealed record RemoteRec(bool On, int S, bool Resuming, bool Queued, bool Finishing);

/// <summary>A Copium upload is going (<paramref name="On"/>) and how far it is (<paramref name="P"/>, percent, in steps of 5).</summary>
internal sealed record RemoteUpload(bool On, int P);

/// <param name="State">"clipsOff" | "starting" | "recording" | "waitingForGame" | "paused" | "error".</param>
/// <param name="BufferS">Seconds of replay the buffer can keep.</param>
internal sealed record RemoteSnapshot(
    string State,
    RemoteRec Rec,
    int BufferS,
    bool Saving,
    bool Screenshots,
    bool Cloud,
    bool SignedIn,
    RemoteUpload Upload,
    bool UpdatePending,
    bool Elevated);

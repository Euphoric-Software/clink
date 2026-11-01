import assert from "node:assert/strict";
import { test } from "node:test";
import { LineSplitter, LineTooLongError, MAX_LINE_BYTES, cmd, hello, parseLine, ping } from "../src/protocol.ts";

test("serialises hello, cmd and ping as single lines", () => {
  assert.deepEqual(JSON.parse(hello("x", "1.0", "bm9uY2U=")), { t: "hello", proto: 1, client: "x", v: "1.0", nonce: "bm9uY2U=" });
  assert.equal(cmd(3, "clip"), '{"t":"cmd","id":3,"verb":"clip"}\n');
  assert.equal(ping(), '{"t":"ping"}\n');
  assert.equal(hello("x", "1", "n").split("\n").length, 2);
});

test("parses every server message and ignores unknown fields", () => {
  assert.deepEqual(parseLine('{"t":"welcome","proto":1,"app":"Clink","version":"1","dev":false,"elevated":true,"verbs":["clip"],"extra":5}'), {
    t: "welcome", proto: 1, app: "Clink", version: "1", dev: false, elevated: true, verbs: ["clip"],
  });
  const w = parseLine('{"t":"welcome","proto":1,"app":"Clink","proof":"QUJD"}');
  assert.ok(w && w.t === "welcome");
  assert.equal(w.proof, "QUJD");
  const np = parseLine('{"t":"welcome","proto":1,"app":"Clink","proof":5}');
  assert.ok(np && np.t === "welcome");
  assert.equal(np.proof, undefined);
  assert.deepEqual(parseLine('{"t":"ack","id":4,"ok":false,"error":"rateLimited"}'), { t: "ack", id: 4, ok: false, error: "rateLimited" });
  assert.deepEqual(parseLine('{"t":"ack","id":4,"ok":true}'), { t: "ack", id: 4, ok: true });
  assert.deepEqual(parseLine('{"t":"bye","reason":"proto","max":1}'), { t: "bye", reason: "proto", max: 1 });
  assert.deepEqual(parseLine('{"t":"pong"}'), { t: "pong" });
  assert.deepEqual(parseLine('{"t":"clipSaved","seconds":30}'), { t: "clipSaved", seconds: 30 });
  assert.deepEqual(parseLine('{"t":"clipNotSaved","reason":"paused"}'), { t: "clipNotSaved", reason: "paused" });
  assert.deepEqual(parseLine('{"t":"shareProgress","p":40}'), { t: "shareProgress", p: 40 });
  assert.deepEqual(parseLine('{"t":"shareDone","outcome":"linkCopied"}'), { t: "shareDone", outcome: "linkCopied" });
  const s = parseLine('{"t":"state","state":"paused","rec":{"on":true,"s":5},"bufferS":9,"upload":{"on":true,"p":50},"newField":1}');
  assert.ok(s && s.t === "state");
  assert.equal(s.state, "paused");
  assert.equal(s.rec.s, 5);
  assert.equal(s.rec.resuming, false);
  assert.equal(s.upload.p, 50);
});

test("unknown types and malformed lines parse to null", () => {
  assert.equal(parseLine('{"t":"somethingNew"}'), null);
  assert.equal(parseLine("not json"), null);
  assert.equal(parseLine("[1]"), null);
  assert.equal(parseLine('{"x":1}'), null);
  assert.equal(parseLine('{"t":"ack","ok":true}'), null);
});

test("LineSplitter splits across chunks and accepts CR LF", () => {
  const sp = new LineSplitter();
  assert.deepEqual(sp.push(Buffer.from('{"a":1}\r\n{"b"')), ['{"a":1}']);
  assert.deepEqual(sp.push(Buffer.from(":2}\n\n")), ['{"b":2}', ""]);
});

test("LineSplitter throws past the cap, before the line ends", () => {
  const sp = new LineSplitter();
  assert.throws(() => sp.push(Buffer.alloc(MAX_LINE_BYTES + 2, 0x78)), LineTooLongError);
  const ok = new LineSplitter();
  assert.equal(ok.push(Buffer.concat([Buffer.alloc(MAX_LINE_BYTES, 0x78), Buffer.from("\n")])).length, 1);
  assert.throws(() => new LineSplitter().push(Buffer.concat([Buffer.alloc(MAX_LINE_BYTES + 1, 0x78), Buffer.from("\n")])), LineTooLongError);
});
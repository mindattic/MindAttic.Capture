/*
 * MindAttic.Capture — one JavaScript API for pages inside a WebView2 host (MindAttic Browser, Automata, any
 * app using MindAttic.Capture.WebView2) and for plain browser pages talking to `capture serve` on localhost.
 *
 *   mindattic.capture.raise('critical-hit', { damage: 912 });   // trigger rules from capture.json
 *   mindattic.capture.marker('boss-phase-2');
 *   await mindattic.capture.screenshot('victory');
 *   await mindattic.capture.clip(10, 'devlog');
 *   await mindattic.capture.replay(15, 5, 'boss-death');
 *   await mindattic.capture.startRecording('level-1'); ... await mindattic.capture.stopRecording();
 *   await mindattic.capture.status();
 *
 * Every call returns a Promise. When no capture host is reachable, calls resolve to { ok: false, error }.
 */
(function () {
  'use strict';
  if (window.mindattic && window.mindattic.capture) return;

  var pending = new Map();
  var nextId = 1;
  var transport = null;

  function webViewTransport() {
    var wv = window.chrome && window.chrome.webview;
    if (!wv) return null;
    wv.addEventListener('message', function (e) {
      var msg = e.data;
      if (!msg || !msg.mindatticCaptureReply) return;
      var reply = msg.mindatticCaptureReply;
      var p = pending.get(reply.id);
      if (p) { pending.delete(reply.id); p(reply.result); }
    });
    return function (op, args) {
      return new Promise(function (resolve) {
        var id = String(nextId++);
        pending.set(id, resolve);
        wv.postMessage({ mindatticCapture: Object.assign({ id: id, op: op }, args) });
      });
    };
  }

  function agentTransport() {
    var url = (window.MINDATTIC_CAPTURE_URL || 'ws://127.0.0.1:47800/ws');
    var socket = null;
    var queue = [];
    function connect() {
      try { socket = new WebSocket(url); } catch (e) { socket = null; return; }
      socket.onopen = function () { queue.splice(0).forEach(function (m) { socket.send(m); }); };
      socket.onmessage = function (e) {
        var msg;
        try { msg = JSON.parse(e.data); } catch (_) { return; }
        var p = pending.get(String(msg.id));
        if (p) { pending.delete(String(msg.id)); p(msg.reply); }
      };
      socket.onclose = function () {
        socket = null;
        queue.length = 0;
        pending.forEach(function (p) { p({ ok: false, error: 'capture agent disconnected' }); });
        pending.clear();
      };
      socket.onerror = function () { };
    }
    return function (op, args) {
      return new Promise(function (resolve) {
        var id = String(nextId++);
        var msg = JSON.stringify(Object.assign({ id: id, op: op }, args));
        pending.set(id, resolve);
        if (!socket) connect();
        if (!socket) { pending.delete(id); resolve({ ok: false, error: 'no capture host (run `capture serve`)' }); return; }
        if (socket.readyState === 1) socket.send(msg); else queue.push(msg);
        setTimeout(function () {
          if (pending.has(id)) { pending.delete(id); resolve({ ok: false, error: 'capture host did not answer' }); }
        }, 120000);
      });
    };
  }

  function send(op, args) {
    if (!transport) transport = webViewTransport() || agentTransport();
    return transport(op, args || {});
  }

  function num(x, dflt) { var n = Number(x); return x == null || x === '' || !isFinite(n) ? dflt : n; }

  var api = {
    raise: function (event, data) { return send('raise', { event: String(event), data: data || null }); },
    marker: function (name, data) { return send('marker', { name: String(name), data: data || null }); },
    screenshot: function (tag, data) { return send('screenshot', { tag: tag || 'screenshot', data: data || null }); },
    clip: function (seconds, tag, data) { return send('clip', { seconds: num(seconds, 10), tag: tag || 'clip', data: data || null }); },
    replay: function (before, after, tag, data) {
      return send('replay', { before: num(before, 15), after: num(after, 0), tag: tag || 'replay', data: data || null });
    },
    startRecording: function (tag, data) { return send('record/start', { tag: tag || 'recording', data: data || null }); },
    stopRecording: function () { return send('record/stop'); },
    pauseRecording: function () { return send('record/pause'); },
    resumeRecording: function () { return send('record/resume'); },
    status: function () { return send('status'); }
  };

  window.mindattic = window.mindattic || {};
  window.mindattic.capture = api;
})();

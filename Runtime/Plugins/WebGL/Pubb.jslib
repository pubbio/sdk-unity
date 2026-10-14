mergeInto(LibraryManager.library, {
  $PubbSockets: { next: 1, sockets: {} },
  Pubb_Connect__deps: ['$PubbSockets'],
  Pubb_Connect: function (url) {
    var id = PubbSockets.next++;
    var entry = { socket: null, queue: [] };
    PubbSockets.sockets[id] = entry;
    try {
      var socket = new WebSocket(UTF8ToString(url));
      entry.socket = socket;
      socket.onmessage = function (event) {
        if (PubbSockets.sockets[id] !== entry) return;
        if (typeof event.data !== 'string' || event.data.length > 1048576 || entry.queue.length >= 256) {
          socket.onmessage = null;
          entry.queue = [{ kind: 2 }];
          socket.close();
          return;
        }
        entry.queue.push({ kind: 0, text: event.data });
      };
      socket.onclose = function () {
        if (PubbSockets.sockets[id] === entry) entry.queue.push({ kind: 1 });
      };
      socket.onerror = function () {
        if (PubbSockets.sockets[id] === entry) entry.queue.push({ kind: 2 });
      };
    } catch (_) { entry.queue.push({ kind: 2 }); }
    return id;
  },
  Pubb_Send__deps: ['$PubbSockets'],
  Pubb_Send: function (id, message) {
    var entry = PubbSockets.sockets[id];
    if (!entry || !entry.socket || entry.socket.readyState !== 1 || entry.socket.bufferedAmount > 65536) return -1;
    try { entry.socket.send(UTF8ToString(message)); return 0; }
    catch (_) { return -1; }
  },
  Pubb_Poll__deps: ['$PubbSockets'],
  Pubb_Poll: function (id, length) {
    HEAP32[length >> 2] = 0;
    var entry = PubbSockets.sockets[id];
    if (!entry || entry.queue.length === 0) return 0;
    var json = JSON.stringify(entry.queue.shift());
    var size = lengthBytesUTF8(json);
    var pointer = _malloc(size + 1);
    if (!pointer) return 0;
    stringToUTF8(json, pointer, size + 1);
    HEAP32[length >> 2] = size;
    return pointer;
  },
  Pubb_Free: function (pointer) { _free(pointer); },
  Pubb_Close__deps: ['$PubbSockets'],
  Pubb_Close: function (id) {
    var entry = PubbSockets.sockets[id];
    if (!entry) return;
    delete PubbSockets.sockets[id];
    if (entry.socket) {
      entry.socket.onmessage = entry.socket.onclose = entry.socket.onerror = null;
      entry.socket.close();
    }
  }
});

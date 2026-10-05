mergeInto(LibraryManager.library, {
  $ZRAssetMini: {
    next: 1, jobs: {}, copyQueue: [], copyActive: 0,
    finishDownload: function(job, state, error) {
      if (job.cleaned) return;
      job.cleaned = true;
      var cleanupError = null;
      if (job.fd != null) { try { job.fs.closeSync({fd: job.fd}); } catch (e) { cleanupError = e; } job.fd = null; }
      if (job.stream) { try { FS.close(job.stream); } catch (e) { cleanupError = e; } job.stream = null; }
      if (state === 1 && cleanupError) { state = -1; error = cleanupError; }
      if (state === 1) {
        try { FS.rename(job.stage, job.output); } catch (e) { state = -1; error = e; }
      }
      if (state !== 1 && job.stage) { try { FS.unlink(job.stage); } catch (ignored) {} }
      if (job.path) { try { job.fs.unlinkSync(job.path); } catch (ignored) {} job.path = null; }
      job.buffer = null;
      job.state = state;
      job.error = error ? String(error.errMsg || error.message || error).slice(0, 128) : null;
      job.copying = false;
      if (job.copyStarted) { job.copyStarted = false; ZRAssetMini.copyActive--; }
      // Queue progression is asynchronous, including canceled queued jobs.
      setTimeout(ZRAssetMini.pumpCopies, 0);
    },
    pumpCopies: function() {
      if (ZRAssetMini.copyActive || !ZRAssetMini.copyQueue.length) return;
      var job = ZRAssetMini.copyQueue.shift();
      if (job.cleaned) { setTimeout(ZRAssetMini.pumpCopies, 0); return; }
      ZRAssetMini.copyActive++;
      job.copyStarted = true;
      try {
        var stat = job.fs.statSync(job.path), size = (stat.stats || stat).size;
        if (!Number.isSafeInteger(size) || size < 0) throw new Error('SDK returned invalid file size');
        if (size > job.maximum) { ZRAssetMini.finishDownload(job, -2, 'Download exceeds manifest size'); return; }
        job.size = size; job.offset = 0;
        if (job.fs.openSync && job.fs.readSync && job.fs.closeSync) {
          job.fd = job.fs.openSync({filePath: job.path, flag: 'r'});
          job.buffer = new Uint8Array(256 * 1024);
        } else {
          if (size > job.fallbackMaximum) throw new Error('SDK lacks ranged file reads; use smaller bundles or a platform file backend');
          var data = job.fs.readFileSync(job.path);
          job.buffer = data instanceof ArrayBuffer ? new Uint8Array(data) : ArrayBuffer.isView(data) ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength) : null;
          if (!job.buffer || job.buffer.length !== size) throw new Error('SDK returned invalid binary file');
        }
        job.stream = FS.open(job.stage, 'w');
        setTimeout(function() { ZRAssetMini.copySlice(job); }, 0);
      } catch (error) { ZRAssetMini.finishDownload(job, -1, error); }
    },
    copySlice: function(job) {
      if (job.cleaned) return;
      try {
        var count = Math.min(256 * 1024, job.size - job.offset), sourceOffset = job.offset;
        if (job.fd != null && count) {
          var read = job.fs.readSync({fd: job.fd, arrayBuffer: job.buffer.buffer, offset: 0, length: count, position: job.offset});
          count = typeof read === 'number' ? read : read.bytesRead;
          if (!Number.isInteger(count) || count <= 0 || count > Math.min(job.buffer.length, job.size - job.offset)) throw new Error('SDK ranged read ended early');
          sourceOffset = 0;
        }
        if (count && FS.write(job.stream, job.buffer, sourceOffset, count, job.offset) !== count) throw new Error('MEMFS short write');
        job.offset += count;
        if (job.offset === job.size) { job.bytes = job.size; ZRAssetMini.finishDownload(job, 1); }
        else setTimeout(function() { ZRAssetMini.copySlice(job); }, 0);
      } catch (error) { ZRAssetMini.finishDownload(job, -1, error); }
    },
    // SHA-256 per 256 KiB chunk: no whole-file copy and no timestamp assumptions.
    hash: function(bytes) {
      var k = [0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2];
      var h = [0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19];
      var w = new Int32Array(64), length = bytes.length, padded = (length + 72) & ~63;
      function r(x, n) { return (x >>> n) | (x << (32 - n)); }
      for (var offset = 0; offset < padded; offset += 64) {
        w.fill(0);
        for (var i = 0; i < 64; i++) {
          var position = offset + i;
          var value = position < length ? bytes[position] : position === length ? 128 : 0;
          w[i >>> 2] |= value << (24 - (i & 3) * 8);
        }
        if (offset + 64 === padded) w[15] = length * 8;
        for (var j = 16; j < 64; j++) {
          var x = w[j - 15], y = w[j - 2];
          w[j] = (w[j - 16] + (r(x,7) ^ r(x,18) ^ (x >>> 3)) + w[j - 7] + (r(y,17) ^ r(y,19) ^ (y >>> 10))) | 0;
        }
        var a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], z = h[7];
        for (var t = 0; t < 64; t++) {
          var first = (z + (r(e,6) ^ r(e,11) ^ r(e,25)) + ((e & f) ^ (~e & g)) + k[t] + w[t]) | 0;
          var second = ((r(a,2) ^ r(a,13) ^ r(a,22)) + ((a & b) ^ (a & c) ^ (b & c))) | 0;
          z = g; g = f; f = e; e = (d + first) | 0; d = c; c = b; b = a; a = (first + second) | 0;
        }
        // This is the hot loop: avoid an array and callback allocation per 64 bytes.
        h[0] = (h[0] + a) | 0; h[1] = (h[1] + b) | 0;
        h[2] = (h[2] + c) | 0; h[3] = (h[3] + d) | 0;
        h[4] = (h[4] + e) | 0; h[5] = (h[5] + f) | 0;
        h[6] = (h[6] + g) | 0; h[7] = (h[7] + z) | 0;
      }
      return h.map(function(v) { return ('00000000' + (v >>> 0).toString(16)).slice(-8); }).join('');
    },
    sdk: function(host) {
      var sdk = host === 0 ? (typeof wx !== 'undefined' && wx) : host === 1 ? (typeof tt !== 'undefined' && tt) :
        host === 2 ? (typeof my !== 'undefined' && my) : host === 3 ? (typeof qg !== 'undefined' && qg) : (typeof ks !== 'undefined' && ks);
      if (!sdk || typeof sdk.getFileSystemManager !== 'function') throw new Error('Unsupported mini game SDK file API');
      return sdk;
    }
  },
  ZRAssetMiniDownload__deps: ['$ZRAssetMini', '$FS'],
  ZRAssetMiniDownload: function(host, urlPtr, outputPtr, maximum, headersPtr, fallbackMaximum) {
    var id = ZRAssetMini.next++, output = UTF8ToString(outputPtr);
    var job = {state: 0, bytes: 0, statusCode: 0, error: null, retryAfter: null, output: output,
      stage: output + '.sdk-' + id + '.copying', maximum: maximum, fallbackMaximum: fallbackMaximum};
    ZRAssetMini.jobs[id] = job;
    function metadata(response) {
      if (!response) return;
      if (Number.isInteger(response.statusCode)) job.statusCode = response.statusCode;
      var headers = response.header || response.headers || {};
      Object.keys(headers).forEach(function(name) { if (name.toLowerCase() === 'retry-after') job.retryAfter = String(headers[name]).slice(0, 96); });
    }
    try {
      var sdk = ZRAssetMini.sdk(host), headers = {};
      job.fs = sdk.getFileSystemManager();
      var headerPayload = JSON.parse(UTF8ToString(headersPtr));
      (headerPayload.Items || headerPayload.items).forEach(function(p) {
        headers[p.Key === undefined ? p.key : p.Key] = p.Value === undefined ? p.value : p.Value;
      });
      job.task = sdk.downloadFile({url: UTF8ToString(urlPtr), header: headers,
        success: function(response) {
          var path = response.tempFilePath || response.apFilePath || response.filePath;
          if (job.cleaned) { if (path) { try { job.fs.unlinkSync(path); } catch (ignored) {} } return; }
          metadata(response); job.path = path;
          if (job.statusCode && job.statusCode !== 200) { ZRAssetMini.finishDownload(job, -1, 'HTTP ' + job.statusCode); return; }
          // SDKs omitting HTTP status on a successful download retain the previous 200 semantics.
          job.statusCode = 200;
          job.copying = true;
          ZRAssetMini.copyQueue.push(job);
          setTimeout(ZRAssetMini.pumpCopies, 0);
        }, fail: function(response) {
          if (job.cleaned) return;
          metadata(response);
          ZRAssetMini.finishDownload(job, -1, response && response.errMsg || 'SDK download failed');
        }
      });
      if (job.task && job.task.onHeadersReceived) job.task.onHeadersReceived(function(response) { if (!job.cleaned) metadata(response); });
      if (job.task && job.task.onProgressUpdate) job.task.onProgressUpdate(function(p) {
        if (job.cleaned || job.copying) return;
        var bytes = p.totalBytesWritten;
        if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes < 0) return;
        job.bytes = bytes;
        if (job.bytes > maximum) {
          ZRAssetMini.finishDownload(job, -2, 'Download exceeds manifest size');
          if (job.task.abort) job.task.abort();
        }
      });
    } catch (error) { ZRAssetMini.finishDownload(job, -1, error); }
    return id;
  },
  ZRAssetMiniBytes__deps: ['$ZRAssetMini'],
  ZRAssetMiniBytes: function(id) { return ZRAssetMini.jobs[id] ? ZRAssetMini.jobs[id].bytes : 0; },
  ZRAssetMiniState__deps: ['$ZRAssetMini'],
  ZRAssetMiniState: function(id) { return ZRAssetMini.jobs[id] ? ZRAssetMini.jobs[id].state : -1; },
  ZRAssetMiniCopying__deps: ['$ZRAssetMini'],
  ZRAssetMiniCopying: function(id) { return ZRAssetMini.jobs[id] && ZRAssetMini.jobs[id].copying ? 1 : 0; },
  ZRAssetMiniDetails__deps: ['$ZRAssetMini', '$stringToUTF8'],
  ZRAssetMiniDetails: function(id, bufferPtr, capacity) {
    var job = ZRAssetMini.jobs[id] || {};
    stringToUTF8(JSON.stringify({StatusCode: job.statusCode || 0, Error: job.error || null, RetryAfter: job.retryAfter || null}), bufferPtr, capacity);
  },
  ZRAssetMiniAbort__deps: ['$ZRAssetMini', '$FS'],
  ZRAssetMiniAbort: function(id) {
    var job = ZRAssetMini.jobs[id];
    if (job && !job.state) {
      ZRAssetMini.finishDownload(job, -1, 'Download canceled');
      if (job.task && job.task.abort) { try { job.task.abort(); } catch (ignored) {} }
    }
  },
  ZRAssetMiniDownloadPoll__deps: ['$ZRAssetMini'],
  ZRAssetMiniDownloadPoll: function(id) { var j = ZRAssetMini.jobs[id]; if (!j) return -1; var s = j.state; if (s) delete ZRAssetMini.jobs[id]; return s; },
  ZRAssetMiniSync__deps: ['$ZRAssetMini', '$FS'],
  ZRAssetMiniSync: function(host, spacePtr, sdkRootPtr, rootPtr, restore, rootsPtr) {
    var id = ZRAssetMini.next++, job = {state: 0}, root = UTF8ToString(rootPtr), space = UTF8ToString(spacePtr), base = UTF8ToString(sdkRootPtr);
    ZRAssetMini.jobs[id] = job;
    var rootPayload = JSON.parse(UTF8ToString(rootsPtr));
    var roots = rootPayload.Roots || rootPayload.roots;
    // Each continuation does bounded work. A single SDK call can exceed the budget;
    // chunks are capped at 256 KiB and we yield between chunks/files.
    var iterator = work();
    function step() {
      var start = Date.now();
      try {
        var next;
        do { next = iterator.next(); } while (!next.done && Date.now() - start < 4);
        if (next.done) job.state = 1;
        else setTimeout(step, 0);
      } catch (error) { job.state = -1; }
    }
    setTimeout(step, 0);
    function* work() {
      var sdk = ZRAssetMini.sdk(host), disk = sdk.getFileSystemManager();
      base = base || (sdk.env && sdk.env.USER_DATA_PATH);
      if (!base || !root || root === '/') throw new Error('Storage root required');
      var dir = base.replace(/\/$/, '') + '/' + space;
      try { disk.mkdirSync(dir, true); } catch (ignored) {}
      var invalid = false;
      function validPath(path) { return typeof path === 'string' && path.length > 0 && path.split('/').every(function(p) { return p && p !== '.' && p !== '..' && p.indexOf(':') < 0 && p.indexOf('\\') < 0; }); }
      function chunkName(name) { return /^[a-z0-9]+_[a-z0-9]+_[0-9]+_[0-9]+\.bin$/.test(name); }
      function readIndex(name) {
        var path = dir + '/' + name;
        try { disk.accessSync(path); } catch (missing) { return null; }
        try {
          if (disk.statSync(path).size > 8 * 1024 * 1024) throw new Error('Index too large');
          var value = JSON.parse(disk.readFileSync(path, 'utf8')), paths = Object.create(null);
          if (value.version !== 1 || value.root !== root || !Array.isArray(value.files)) throw new Error('Storage identity mismatch');
          value.files.forEach(function(f) {
            if (!validPath(f.path) || paths[f.path] || !Number.isSafeInteger(f.size) || f.size < 0 || !Array.isArray(f.chunks) ||
                (f.hashes && (!Array.isArray(f.hashes) || f.hashes.length !== f.chunks.length))) throw new Error('Invalid stored file');
            paths[f.path] = true;
            f.chunks.forEach(function(c, i) { if (!chunkName(c) || (f.hashes && !/^[a-f0-9]{64}$/.test(f.hashes[i]))) throw new Error('Invalid chunk'); });
          });
          return value;
        } catch (error) { invalid = true; return null; }
      }
      function readChunk(f, i) {
        var path = dir + '/' + f.chunks[i];
        if (disk.statSync(path).size > 262144) throw new Error('Chunk too large');
        var bytes = new Uint8Array(disk.readFileSync(path));
        if (f.hashes && f.hashes[i] !== ZRAssetMini.hash(bytes)) throw new Error('Stored chunk checksum mismatch');
        return bytes;
      }
      var old = readIndex('index.json'), backup;
      if (restore) {
        // Stage the entire candidate before touching any destination. Failed primary
        // chunks trigger backup validation as well as malformed primary indexes.
        function* recover(index) {
          var staging = root + '/.zr-restore-' + id, staged = [], committed = [];
          try {
            for (var i = 0; i < index.files.length; i++) {
              var entry = index.files[i], path = staging + '/' + i, offset = 0;
              FS.mkdirTree(staging);
              var stream = FS.open(path, 'w'); staged.push(path);
              try {
                for (var n = 0; n < entry.chunks.length; n++) {
                  var bytes = readChunk(entry, n);
                  if (offset + bytes.length > entry.size) throw new Error('Oversized stored file');
                  FS.write(stream, bytes, 0, bytes.length, offset); offset += bytes.length;
                  yield;
                }
                if (offset !== entry.size) throw new Error('Incomplete stored file');
              } finally { FS.close(stream); }
              yield;
            }
            // MEMFS renames require no copying of bundle bytes. Journal old files so
            // even a failed rename can roll back already committed destinations.
            for (var j = 0; j < index.files.length; j++) {
              var dest = root + '/' + index.files[j].path, saved = staging + '/old-' + j;
              FS.mkdirTree(dest.substring(0, dest.lastIndexOf('/')));
              var hadOld = FS.analyzePath(dest).exists;
              if (hadOld) FS.rename(dest, saved);
              committed.push({dest: dest, saved: saved, hadOld: hadOld});
              FS.rename(staged[j], dest);
            }
          } catch (error) {
            for (var k = committed.length - 1; k >= 0; k--) {
              var item = committed[k];
              try { FS.unlink(item.dest); } catch (ignored) {}
              if (item.hadOld) FS.rename(item.saved, item.dest);
            }
            throw error;
          } finally {
            staged.concat(committed.map(function(c) { return c.saved; })).forEach(function(path) { try { FS.unlink(path); } catch (ignored) {} });
            try { FS.rmdir(staging); } catch (ignored) {}
          }
        }
        if (old) { try { yield* recover(old); return; } catch (badPrimary) { invalid = true; } }
        backup = readIndex('backup.json');
        if (backup) { yield* recover(backup); return; }
        if (invalid) throw new Error('No valid recovery snapshot');
        return;
      }
      backup = readIndex('backup.json');
      old = old || backup;
      if (!old && invalid) throw new Error('No valid recovery index');
      var verified = Object.create(null), oldHealthy = true;
      if (old) for (var v = 0; v < old.files.length; v++) {
        var priorFile = old.files[v], priorSize = 0;
        for (var q = 0; q < priorFile.chunks.length; q++) {
          try {
            var priorBytes = readChunk(priorFile, q);
            verified[priorFile.chunks[q]] = {size: priorBytes.length, hash: priorFile.hashes ? priorFile.hashes[q] : ZRAssetMini.hash(priorBytes)};
            priorSize += priorBytes.length;
          } catch (corrupt) { oldHealthy = false; }
          yield;
        }
        if (priorSize !== priorFile.size) oldHealthy = false;
      }
      // A flush from correct memory can repair bad chunks/indexes. Unopened packages
      // are retained only after validating their bytes, never silently discarded.
      var previous = Object.create(null), files = [], fileIndex = 0;
      if (old) old.files.forEach(function(f) { previous[f.path] = f; });
      var generation = Date.now().toString(36) + '_' + id.toString(36), buffer = new Uint8Array(262144);
      function* walk(path, relative) {
        var names = FS.readdir(path).sort();
        for (var i = 0; i < names.length; i++) {
          var name = names[i];
          if (name === '.' || name === '..') continue;
          var full = path + '/' + name, rel = relative + '/' + name, stat = FS.lstat(full);
          if (FS.isDir(stat.mode)) { yield* walk(full, rel); continue; }
          if (!FS.isFile(stat.mode)) throw new Error('Unsupported file type');
          var stream = FS.open(full, 'r'), chunks = [], hashes = [], offset = 0, n = fileIndex++, prior = previous[rel];
          try {
            while (offset < stat.size) {
              var count = FS.read(stream, buffer, 0, Math.min(buffer.length, stat.size - offset), offset);
              if (!count) throw new Error('Unexpected end of file');
              var bytes = buffer.subarray(0, count), hash = ZRAssetMini.hash(bytes), index = chunks.length;
              var chunk = prior && prior.chunks[index], reuse = false;
              var stored = chunk && verified[chunk];
              reuse = stored && stored.size === count && stored.hash === hash;
              if (!reuse) { chunk = generation + '_' + n + '_' + index + '.bin'; disk.writeFileSync(dir + '/' + chunk, bytes.slice().buffer); }
              chunks.push(chunk); hashes.push(hash); offset += count;
              yield;
            }
          } finally { FS.close(stream); }
          files.push({path: rel, size: stat.size, chunks: chunks, hashes: hashes});
          yield;
        }
      }
      var seen = [];
      roots.sort().forEach(function(path) {
        if (path.indexOf(root + '/') !== 0 || !validPath(path.substring(root.length + 1))) throw new Error('Cache outside persistent root');
        if (!seen.some(function(p) { return path === p || path.indexOf(p + '/') === 0; })) seen.push(path);
      });
      for (var r = 0; r < seen.length; r++) {
        if (FS.analyzePath(seen[r]).exists) yield* walk(seen[r], seen[r].substring(root.length + 1));
      }
      if (old) for (var f = 0; f < old.files.length; f++) {
        var entry = old.files[f], full = root + '/' + entry.path;
        if (!seen.some(function(path) { return full.indexOf(path + '/') === 0; })) {
          var size = 0;
          for (var c = 0; c < entry.chunks.length; c++) {
            var kept = verified[entry.chunks[c]];
            if (!kept) throw new Error('Unopened package is corrupt');
            size += kept.size; yield;
          }
          if (size !== entry.size) throw new Error('Unopened package is corrupt');
          files.push(entry);
        }
      }
      files.sort(function(a, b) { return a.path < b.path ? -1 : a.path > b.path ? 1 : 0; });
      var next = JSON.stringify({version: 1, root: root, files: files});
      if (old && next === JSON.stringify(old)) return;
      disk.writeFileSync(dir + '/next.json', next, 'utf8');
      if (old && oldHealthy) disk.writeFileSync(dir + '/backup.json', JSON.stringify(old), 'utf8');
      try { disk.unlinkSync(dir + '/index.json'); } catch (ignored) {}
      disk.renameSync(dir + '/next.json', dir + '/index.json');
      var keep = Object.create(null);
      var retained = oldHealthy ? old : backup;
      files.concat(retained ? retained.files : []).forEach(function(f) { f.chunks.forEach(function(c) { keep[c] = true; }); });
      var storedNames = disk.readdirSync(dir);
      for (var d = 0; d < storedNames.length; d++) {
        var name = storedNames[d];
        if (chunkName(name) && !keep[name]) { try { disk.unlinkSync(dir + '/' + name); } catch (ignored) {} }
        if (d % 64 === 63) yield;
      }
    }
    return id;
  },
  ZRAssetMiniPoll__deps: ['$ZRAssetMini'],
  ZRAssetMiniPoll: function(id) { var j = ZRAssetMini.jobs[id]; if (!j) return -1; var s = j.state; if (s) delete ZRAssetMini.jobs[id]; return s; }
});

mergeInto(LibraryManager.library, {
  $ZRAssetStorage: { next: 1, states: {}, locked: false, release: null },
  ZRAssetSyncBegin__deps: ['$ZRAssetStorage', '$FS'],
  ZRAssetSyncBegin: function(restore, rootPtr) {
    var root = UTF8ToString(rootPtr), id = ZRAssetStorage.next++;
    ZRAssetStorage.states[id] = 0;
    var sync = function() {
      try { FS.syncfs(!!restore, function(error) { ZRAssetStorage.states[id] = error ? -1 : 1; }); }
      catch (error) { ZRAssetStorage.states[id] = -1; }
    };
    if (ZRAssetStorage.locked) { sync(); return id; }
    if (!restore || typeof navigator === 'undefined' || !navigator.locks) { ZRAssetStorage.states[id] = -1; return id; }
    navigator.locks.request('ZRAsset:' + root, {ifAvailable: true}, function(lock) {
      if (!lock) { ZRAssetStorage.states[id] = -1; return; }
      ZRAssetStorage.locked = true;
      sync();
      return new Promise(function(resolve) { ZRAssetStorage.release = resolve; });
    }).catch(function() { ZRAssetStorage.states[id] = -1; });
    return id;
  },
  ZRAssetSyncPoll__deps: ['$ZRAssetStorage'],
  ZRAssetSyncPoll: function(id) {
    var state = ZRAssetStorage.states[id];
    if (state) delete ZRAssetStorage.states[id];
    return typeof state === 'number' ? state : -1;
  }
});

// Exposes the hosting page's full URL to C# in WebGL builds. PhoneGyroController
// uses it to extract the room ID from `#room=...` and build the wss://host/ws URL so
// each laptop session stays isolated from other students in the room-routed relay.
mergeInto(LibraryManager.library, {
  GetPageUrl: function () {
    var s = window.location.href;
    var len = lengthBytesUTF8(s) + 1;
    var ptr = _malloc(len);
    stringToUTF8(s, ptr, len);
    return ptr;
  }
});

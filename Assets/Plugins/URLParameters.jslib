mergeInto(LibraryManager.library, {
  GetQueryParam: function (paramId) {
    var key = UTF8ToString(paramId);
    var urlParams = new URLSearchParams(window.location.search);
    var paramValue = urlParams.get(key);

    if (paramValue == null) {
        return null;
    }

    var bufferSize = lengthBytesUTF8(paramValue) + 1;
    var buffer = _malloc(bufferSize);
    stringToUTF8(paramValue, buffer, bufferSize);
    return buffer;
  }
});
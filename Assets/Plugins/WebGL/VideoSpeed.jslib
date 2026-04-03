// =============================================================================
// VideoSpeed.jslib
// Place this file at: Assets/Plugins/WebGL/VideoSpeed.jslib
//
// This plugin gives C# direct access to the HTML5 <video> element's
// playbackRate property — bypassing Unity's VideoPlayer wrapper entirely.
//
// Why this is needed:
//   Unity's VideoPlayer.playbackSpeed in WebGL just sets video.playbackRate
//   on the DOM element, but Chrome resets it back to 1.0 internally every
//   time Play() is called. Unity's C# code runs in the next frame by which
//   point the user has already seen the speed flash.
//
//   Calling this jslib function from C# runs SYNCHRONOUSLY in the same JS
//   call stack as Unity's Play() — no frame delay, no flash.
//
// Usage in C#:
//   [System.Runtime.InteropServices.DllImport("__Internal")]
//   private static extern void SetVideoPlaybackRate(float rate);
//   ...
//   SetVideoPlaybackRate(0.75f);
// =============================================================================
mergeInto(LibraryManager.library, {

  SetVideoPlaybackRate: function(rate) {
    // Unity WebGL creates exactly one <video> element for VideoPlayer.
    // We find it and set the rate directly.
    var videos = document.getElementsByTagName('video');
    if (videos.length > 0) {
      videos[0].playbackRate = rate;
    }
  }

});

// Save this file as: Assets/Plugins/WebGL/FileUpload.jslib

mergeInto(LibraryManager.library, {
    OpenFileDialog: function(gameObjectNamePtr, methodNamePtr) {
        var gameObjectName = UTF8ToString(gameObjectNamePtr);
        var methodName = UTF8ToString(methodNamePtr);
        
        // Create file input element
        var fileInput = document.createElement('input');
        fileInput.type = 'file';
        fileInput.accept = 'video/*';
        fileInput.style.display = 'none';
        
        fileInput.onchange = function(event) {
            var file = event.target.files[0];
            if (file) {
                console.log("File selected: " + file.name);
                
                // Create a blob URL for the video
                var blobUrl = URL.createObjectURL(file);
                
                // Send the URL back to Unity
                SendMessage(gameObjectName, methodName, blobUrl);
            }
            
            // Clean up
            document.body.removeChild(fileInput);
        };
        
        // Trigger file dialog
        document.body.appendChild(fileInput);
        fileInput.click();
    }
});
// Monaco Editor Integration for PackArcade2
let editor = null;
let dotNetRef = null;

window.initializeEditor = function (dotNetReference) {
    dotNetRef = dotNetReference;
    
    // Load Monaco if not loaded
    if (!window.monaco) {
        loadMonaco();
    } else {
        setTimeout(createEditor, 100);
    }
};

function loadMonaco() {
    const script = document.createElement('script');
    script.src = 'https://cdn.jsdelivr.net/npm/monaco-editor@0.34.0/min/vs/loader.js';
    script.onload = () => {
        require.config({ paths: { vs: 'https://cdn.jsdelivr.net/npm/monaco-editor@0.34.0/min/vs' } });
        require(['vs/editor/editor.main'], function () {
            createEditor();
        });
    };
    script.onerror = () => {
        console.error('Failed to load Monaco editor');
        // Fallback to textarea if Monaco fails
        createFallbackEditor();
    };
    document.head.appendChild(script);
}

function createEditor() {
    const container = document.getElementById('editorContainer');
    if (!container) {
        console.error('Editor container not found');
        return;
    }

    // Clear container
    container.innerHTML = '';

    editor = monaco.editor.create(container, {
        value: '',
        language: 'html',
        theme: 'vs-dark',
        automaticLayout: true,
        fontSize: 14,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        wordWrap: 'on'
    });

    editor.onDidChangeCursorPosition((e) => {
        if (dotNetRef) {
            dotNetRef.invokeMethodAsync('UpdateCursorPosition', e.position.lineNumber, e.position.column);
        }
    });

    console.log('Monaco editor initialized');
}

function createFallbackEditor() {
    const container = document.getElementById('editorContainer');
    if (!container) return;

    container.innerHTML = '';
    const textarea = document.createElement('textarea');
    textarea.style.width = '100%';
    textarea.style.height = '100%';
    textarea.style.backgroundColor = '#1e1e1e';
    textarea.style.color = '#fff';
    textarea.style.fontFamily = 'monospace';
    textarea.style.fontSize = '14px';
    textarea.style.padding = '10px';
    textarea.style.border = 'none';
    textarea.style.outline = 'none';
    
    textarea.addEventListener('input', function() {
        if (dotNetRef) {
            // Update cursor position roughly
            const lines = this.value.substr(0, this.selectionStart).split('\n');
            dotNetRef.invokeMethodAsync('UpdateCursorPosition', lines.length, this.selectionStart - lines.slice(0, -1).join('\n').length);
        }
    });
    
    container.appendChild(textarea);
    window.fallbackEditor = textarea;
}

window.setEditorContent = function (content) {
    if (editor) {
        editor.setValue(content || '');
    } else if (window.fallbackEditor) {
        window.fallbackEditor.value = content || '';
    }
};

window.getEditorContent = function () {
    if (editor) {
        return editor.getValue();
    } else if (window.fallbackEditor) {
        return window.fallbackEditor.value;
    }
    return '';
};

window.refreshPreview = function (html) {
    const frame = document.getElementById('previewFrame');
    if (frame) {
        frame.srcdoc = html;
    }
};

// Clean up on page unload
window.addEventListener('beforeunload', function() {
    if (editor) {
        editor.dispose();
        editor = null;
    }
});
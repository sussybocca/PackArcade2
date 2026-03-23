// Working DOSBox integration
let ci = null;

window.runEXEGame = async function(subdomain, filename) {
    const canvas = document.getElementById('exeCanvas');
    
    if (!canvas) {
        console.error('Canvas not found');
        return;
    }

    try {
        // Clear canvas
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = '#000';
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.fillStyle = '#fff';
        ctx.font = '20px monospace';
        ctx.fillText('Loading...', 50, 100);

        // Load DOSBox if not loaded
        if (!window.DOSBOX) {
            await loadDOSBox();
        }

        // Fetch the EXE file
        const response = await fetch(`/games/${subdomain}/${filename}`);
        const exeData = await response.arrayBuffer();

        // Create DOSBox instance
        DOSBOX.createCanvas(canvas, async (dosbox) => {
            // Create a DOS filesystem
            await dosbox.fs.createFile('GAME.EXE', new Uint8Array(exeData));
            
            // Run the game
            await dosbox.run('GAME.EXE');
        });
        
    } catch (error) {
        console.error('Failed to run EXE:', error);
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = '#f00';
        ctx.font = '20px monospace';
        ctx.fillText('Error: ' + error.message, 50, 100);
    }
};

window.stopEXEGame = function() {
    if (window.DOSBOX) {
        DOSBOX.quit();
    }
    
    const canvas = document.getElementById('exeCanvas');
    if (canvas) {
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = '#000';
        ctx.fillRect(0, 0, canvas.width, canvas.height);
    }
};

function loadDOSBox() {
    return new Promise((resolve, reject) => {
        if (window.DOSBOX) {
            resolve();
            return;
        }
        
        // Load the DOSBox CSS
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = 'https://cdn.jsdelivr.net/npm/js-dos@7.5.4/dist/js-dos.css';
        document.head.appendChild(link);
        
        // Load the DOSBox script
        const script = document.createElement('script');
        script.src = 'https://cdn.jsdelivr.net/npm/js-dos@7.5.4/dist/js-dos.js';
        script.onload = () => {
            // Wait for DOSBOX to be available
            const checkDOSBOX = setInterval(() => {
                if (window.DOSBOX) {
                    clearInterval(checkDOSBOX);
                    resolve();
                }
            }, 100);
        };
        script.onerror = () => reject(new Error('Failed to load DOSBox'));
        document.head.appendChild(script);
    });
}
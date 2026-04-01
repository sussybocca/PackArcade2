const express = require('express');
const cors = require('cors');
const next = require('next');

const port = parseInt(process.env.PORT, 10) || 3009;
const dev = true;
const nextApp = next({ dev });
const handle = nextApp.getRequestHandler();

nextApp.prepare().then(() => {
    const app = express();

    app.use(cors());
    app.use(express.json());

    // Your existing API routes
    app.get('/api/hello/data', (req, res) => {
        res.json({ items: ['one', 'two', 'three'] });
    });

    // 🔥 Make Next.js handle /api/hello as the root of your app
    app.get('/api/hello', (req, res) => {
        // Rewrite the URL to '/' so Next.js serves the homepage
        req.url = '/';
        return handle(req, res);
    });

    // Let Next.js handle all other routes (if any)
    app.all('*', (req, res) => handle(req, res));

    app.listen(port, '0.0.0.0', () => {
        console.log(`🚀 Server ready on port ${port}`);
    });
});
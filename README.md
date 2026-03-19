# PackArcade2 🎮

**Create, Host, and Share Games & Websites Instantly**

PackArcade2 is a free, open-source platform that lets anyone create HTML games, upload EXE games (playable in browser), and build full websites with custom subdomains. No coding experience required - our drag-and-drop editor makes it easy!

## ✨ Features

- **Instant Subdomains** - Every project gets `yourname.packarcade.win`
- **EXE Game Support** - Upload and play DOS games in browser via emulation
- **Visual Editor** - Drag-and-drop site builder with live preview
- **Code Editor** - Full-featured Monaco editor for HTML/CSS/JS/Python/C#
- **Backend Support** - Run Node.js, Python, or C# servers for your projects
- **File Management** - Upload, create, and organize project files
- **Templates** - Start quickly with pre-built templates
- **Game Uploads** - Share your HTML games with the world

## 🚀 Quick Start

```bash
# Clone the repository
git clone https://github.com/yourusername/packarcade2.git

# Navigate to project
cd packarcade2

# Run the application
dotnet run
Visit http://localhost:5000 to start creating!

📦 Tech Stack
Backend: ASP.NET Core Blazor Server

Database: File-based storage (no database required)

Emulation: DOSBox / Boxedwine for EXE games

Editor: Monaco (VS Code)

Hosting: Cloudflare Tunnel for public access

🏗️ Project Structure
text
PackArcade2/
├── Pages/              # Blazor pages
├── Services/           # Backend services
├── Middleware/         # Request middleware
├── wwwroot/           # Static files
│   ├── css/           # Stylesheets
│   ├── js/            # JavaScript
│   ├── games/         # Uploaded games
│   └── projects/      # User projects
├── Program.cs         # Application entry
└── README.md          # This file
🤝 Contributing
We welcome contributions! Please read our CONTRIBUTING.md first.

Fork the repository

Create your feature branch (git checkout -b feature/AmazingFeature)

Commit changes (git commit -m 'Add AmazingFeature')

Push to branch (git push origin feature/AmazingFeature)

Open a Pull Request

📄 License
This project is licensed under the MIT License - see the LICENSE file for details.

📬 Contact
Bug Reports: support@packarcade.win

General Inquiries: hello@packarcade.win

Spam/Anything: personal@packarcade.win (we actually read this!)

🙏 Acknowledgments
DOSBox for emulation

Monaco Editor for the code editor

Cloudflare for tunnel and DNS

All our contributors and users

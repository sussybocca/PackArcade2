@echo off
echo ========================================
echo CI/CD Pipeline - Build Script
echo ========================================
echo.
echo [1/3] Installing dependencies...
if exist package.json (
    call npm install
) else if exist requirements.txt (
    pip install -r requirements.txt
) else (
    echo No package manager detected. Skipping...
)
echo.
echo [2/3] Running tests...
if exist package.json (
    call npm test
) else if exist test/ (
    python -m pytest
) else (
    echo No tests found. Skipping...
)
echo.
echo [3/3] Building project...
if exist package.json (
    call npm run build
) else (
    echo No build script found. Skipping...
)
echo.
echo ========================================
echo CI/CD Pipeline Complete!
echo ========================================
pause
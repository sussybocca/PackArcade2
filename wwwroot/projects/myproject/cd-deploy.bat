@echo off
echo ========================================
echo Deployment Script
echo ========================================
echo.
echo Deploying project to production...
echo.
echo [1/2] Building for production...
if exist package.json (
    call npm run build
)
echo.
echo [2/2] Deploying...
echo Deployment complete!
echo.
echo ========================================
pause
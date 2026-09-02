@echo off
echo === VintageRTX Normal Map Generator ===
echo.

REM Check if source directory exists
if "%1"=="" (
    echo Usage: generate-normalmaps.bat [source_directory] [strength]
    echo Example: generate-normalmaps.bat "C:\Vintagestory\assets\survival\textures\block" 1.0
    echo.
    echo Default: Will use Vintage Story installation directory
    pause
    exit /b 1
)

set SOURCE_DIR=%1
set STRENGTH=1.0
if not "%2"=="" set STRENGTH=%2

set OUTPUT_DIR=..\assets\vintagertx\textures\normalmaps

echo Source: %SOURCE_DIR%
echo Output: %OUTPUT_DIR%
echo Strength: %STRENGTH%
echo.

REM Compile the generator if needed
if not exist GenerateNormalMaps.exe (
    echo Compiling normal map generator...
    csc /out:GenerateNormalMaps.exe GenerateNormalMaps.cs
    if errorlevel 1 (
        echo Failed to compile generator
        pause
        exit /b 1
    )
)

REM Process block textures
echo Processing block textures...
GenerateNormalMaps.exe "%SOURCE_DIR%\block" "%OUTPUT_DIR%\block" %STRENGTH%

REM Process item textures
echo.
echo Processing item textures...
GenerateNormalMaps.exe "%SOURCE_DIR%\item" "%OUTPUT_DIR%\item" %STRENGTH%

REM Process entity textures
echo.
echo Processing entity textures...
GenerateNormalMaps.exe "%SOURCE_DIR%\entity" "%OUTPUT_DIR%\entity" %STRENGTH%

echo.
echo === Normal map generation complete! ===
echo Generated files are in: %OUTPUT_DIR%
echo.
pause
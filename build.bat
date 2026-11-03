@echo off
rem Compile SpaceLite.cs avec le compilateur C# fourni par Windows (.NET Framework 4).
rem Aucune installation requise. Produit SpaceLite.exe dans ce dossier.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe introuvable : .NET Framework 4 requis.
  exit /b 1
)
set ICON=
if exist "%~dp0SpaceLite.ico" set ICON=/win32icon:"%~dp0SpaceLite.ico"
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 %ICON% /out:"%~dp0SpaceLite.exe" ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:Microsoft.VisualBasic.dll ^
  "%~dp0SpaceLite.cs"
if errorlevel 1 (
  echo.
  echo Echec de la compilation.
  if /i not "%~1"=="nopause" pause
  exit /b 1
)
echo OK : SpaceLite.exe cree.

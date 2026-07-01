@ECHO OFF
call "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\VsDevCmd.bat">nul
@ECHO OFF

SET outputDirectory=%~dp0\Publish

del %outputDirectory%\RePKG_Re.zip
msbuild /p:OutputPath="%outputDirectory%" /p:Configuration=Release RePKG_Re

move %outputDirectory%\RePKG_Re.exe %outputDirectory%\input.exe
ilrepack /out:"%outputDirectory%\RePKG_Re.exe" /wildcards /parallel "%outputDirectory%\input.exe" "%outputDirectory%\*.dll"
del %outputDirectory%\input.exe
del %outputDirectory%\*.dll
del %outputDirectory%\*.pdb
del %outputDirectory%\*.config
del %outputDirectory%\*.json
cd %outputDirectory%
7z a -tzip RePKG_Re.zip *
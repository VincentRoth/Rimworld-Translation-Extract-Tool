@echo off
rem Usage : extract.cmd <mod> [options]   (voir README.md)
dotnet run --project "%~dp0TranslationExtractor.csproj" -c Release -- %*

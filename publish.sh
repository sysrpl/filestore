#!/bin/bash
dotnet publish -c Release -r linux-x64 --self-contained -o ~/.local/share/s3-file-explorer

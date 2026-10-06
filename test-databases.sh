#!/usr/bin/env bash
set -euo pipefail
# テスト専用。SQL ServerのEULAに同意して起動します。
docker run -d --name evidence-pg -e POSTGRES_PASSWORD=EvidenceTest123 -e POSTGRES_DB=evidence -p 127.0.0.1:55432:5432 postgres:16
docker run -d --name evidence-sql -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=EvidenceTest123!' -p 127.0.0.1:51433:1433 mcr.microsoft.com/mssql/server:2022-latest

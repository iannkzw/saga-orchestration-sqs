-- Script de inicialização do banco de dados saga_db.
-- Habilita a extensão uuid-ossp para geração de UUIDs nativos pelo PostgreSQL.
-- As tabelas serão criadas pelas migrations do EF Core, não aqui.

CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

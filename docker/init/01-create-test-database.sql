-- Executado apenas quando o volume é criado pela primeira vez.
-- O banco de testes fica isolado do banco usado durante o desenvolvimento.
SELECT 'CREATE DATABASE postgres_executor_test'
WHERE NOT EXISTS (
    SELECT FROM pg_database WHERE datname = 'postgres_executor_test'
)\gexec

COMMENT ON DATABASE postgres_executor_test IS
    'Banco descartável para testes do PostgreSQL Command Executer';

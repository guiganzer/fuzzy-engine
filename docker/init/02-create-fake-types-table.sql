-- Catálogo de demonstração: uma única tabela com tipos nativos representativos.
-- Este arquivo pode ser executado de novo pelo teste de integração; por isso a
-- tabela é recriada para manter o conjunto de dados conhecido e repetível.
\connect postgres_executor_test

DROP TABLE IF EXISTS public.postgresql_type_showcase;

CREATE TABLE public.postgresql_type_showcase
(
    sample_id              bigint PRIMARY KEY,
    external_id            uuid NOT NULL UNIQUE,
    is_active              boolean NOT NULL,
    priority_smallint      smallint NOT NULL,
    quantity_integer       integer NOT NULL,
    total_bigint           bigint NOT NULL,
    amount_numeric         numeric(18, 4) NOT NULL,
    price_decimal          decimal(12, 2) NOT NULL,
    legacy_money           money NOT NULL,
    ratio_real             real NOT NULL,
    score_double           double precision NOT NULL,
    fixed_code             char(6) NOT NULL,
    short_name             varchar(80) NOT NULL,
    description_text       text,
    binary_payload         bytea,
    created_on             date NOT NULL,
    local_time             time without time zone NOT NULL,
    offset_time            time with time zone NOT NULL,
    created_at             timestamp without time zone NOT NULL,
    observed_at            timestamp with time zone NOT NULL,
    retention_period       interval NOT NULL,
    network_address        inet NOT NULL,
    network_block          cidr NOT NULL,
    device_mac             macaddr NOT NULL,
    device_mac_extended    macaddr8 NOT NULL,
    feature_mask           bit(8) NOT NULL,
    variable_mask          bit varying(16) NOT NULL,
    location_point         point NOT NULL,
    route_line             line NOT NULL,
    route_segment          lseg NOT NULL,
    area_box               box NOT NULL,
    travel_path            path NOT NULL,
    coverage_polygon       polygon NOT NULL,
    coverage_circle        circle NOT NULL,
    search_vector          tsvector NOT NULL,
    search_query           tsquery NOT NULL,
    event_data_json        json NOT NULL,
    profile_jsonb          jsonb NOT NULL,
    large_payload_jsonb    jsonb NOT NULL,
    document_xml           xml,
    integer_values         integer[] NOT NULL,
    tag_values             text[] NOT NULL,
    uuid_values            uuid[] NOT NULL,
    jsonb_values           jsonb[] NOT NULL,
    active_window          int4range NOT NULL,
    amount_window          numrange NOT NULL,
    date_window            daterange NOT NULL,
    timestamp_window       tsrange NOT NULL,
    observed_window        tstzrange NOT NULL,
    integer_windows        int4multirange NOT NULL,
    amount_windows         nummultirange NOT NULL,
    date_windows           datemultirange NOT NULL,
    timestamp_windows      tsmultirange NOT NULL,
    observed_windows       tstzmultirange NOT NULL,
    optional_note          text
);

COMMENT ON TABLE public.postgresql_type_showcase IS
    'Dados artificiais para visualizar tipos PostgreSQL no PostgreSQL Command Executer.';

COMMENT ON COLUMN public.postgresql_type_showcase.large_payload_jsonb IS
    'JSON aninhado propositalmente longo para testar a aba JSON.';

--------------------------------------------------------------
-- HydroPilot - DDL PostgreSQL v2
-- Grupo 5301 - K5053 - UTN


--------------------------------------------------------------
-- CATÁLOGOS / LOOKUPS

CREATE TABLE rol (
    id          SERIAL PRIMARY KEY,
    nombre      VARCHAR(100) NOT NULL UNIQUE,
    descripcion TEXT
);

CREATE TABLE estado_lote (
    id     SERIAL PRIMARY KEY,
    nombre VARCHAR(50) NOT NULL UNIQUE  -- ACTIVO, COSECHADO, DESCARTADO, EN_PAUSA
);

CREATE TABLE tipo_alerta (
    id          SERIAL PRIMARY KEY,
    nombre      VARCHAR(100) NOT NULL UNIQUE,
    descripcion TEXT
);

CREATE TABLE tipo_cultivo (
    id                      SERIAL PRIMARY KEY,
    nombre                  VARCHAR(100) NOT NULL,
    gdd_objetivo            NUMERIC(8,2),
    ph_optimo_min           NUMERIC(4,2),
    ph_optimo_max           NUMERIC(4,2),
    ec_optima_min           NUMERIC(6,2),
    ec_optima_max           NUMERIC(6,2),
    dias_estimados_cosecha  INTEGER,
    descripcion             TEXT
);

CREATE TABLE etapa_fenologica (
    id              SERIAL PRIMARY KEY,
    tipo_cultivo_id INTEGER NOT NULL REFERENCES tipo_cultivo(id),
    nombre          VARCHAR(100) NOT NULL,
    orden           INTEGER NOT NULL,
    descripcion     TEXT
);

CREATE TABLE tipo_insumo (
    id     SERIAL PRIMARY KEY,
    nombre VARCHAR(100) NOT NULL UNIQUE
);

CREATE TABLE insumo (
    id              SERIAL PRIMARY KEY,
    tipo_insumo_id  INTEGER REFERENCES tipo_insumo(id),
    nombre          VARCHAR(150) NOT NULL,
    unidad          VARCHAR(30),
    costo_unitario  NUMERIC(10,2),
    proveedor       VARCHAR(150)
);

CREATE TABLE tipo_anomalia (
    id          SERIAL PRIMARY KEY,
    nombre      VARCHAR(100) NOT NULL UNIQUE,
    descripcion TEXT
);

CREATE TABLE severidad (
    id     SERIAL PRIMARY KEY,
    nombre VARCHAR(50) NOT NULL UNIQUE  -- BAJA, MEDIA, ALTA, CRITICA
);

CREATE TABLE tipo_sensor (
    id     SERIAL PRIMARY KEY,
    nombre VARCHAR(100) NOT NULL UNIQUE
);

CREATE TABLE tipo_actuador (
    id     SERIAL PRIMARY KEY,
    nombre VARCHAR(100) NOT NULL UNIQUE
);

CREATE TABLE unidad_medida (
    id      SERIAL PRIMARY KEY,
    nombre  VARCHAR(50) NOT NULL UNIQUE,
    simbolo VARCHAR(10)
);


--------------------------------------------------------------
-- USUARIOS Y SEGURIDAD

CREATE TABLE usuario (
    id                       SERIAL PRIMARY KEY,
    nombre                   VARCHAR(100) NOT NULL,
    apellido                 VARCHAR(100) NOT NULL,
    email                    VARCHAR(255) NOT NULL UNIQUE,
    sso_proveedor            VARCHAR(50),
    doble_factor_habilitado  BOOLEAN NOT NULL DEFAULT FALSE,
    fecha_alta               TIMESTAMP NOT NULL DEFAULT NOW(),
    ultimo_acceso            TIMESTAMP
);

CREATE TABLE usuario_rol (
    usuario_id  INTEGER NOT NULL REFERENCES usuario(id),
    rol_id      INTEGER NOT NULL REFERENCES rol(id),
    PRIMARY KEY (usuario_id, rol_id)
);

CREATE TABLE log_auditoria (
    id                SERIAL PRIMARY KEY,
    usuario_id        INTEGER NOT NULL REFERENCES usuario(id),
    accion            VARCHAR(100) NOT NULL,
    entidad_afectada  VARCHAR(100),
    fecha_hora        TIMESTAMP NOT NULL DEFAULT NOW(),
    detalle           TEXT
);


--------------------------------------------------------------
-- INFRAESTRUCTURA FÍSICA

CREATE TABLE invernadero (
    id          SERIAL PRIMARY KEY,
    usuario_id  INTEGER NOT NULL REFERENCES usuario(id),
    nombre      VARCHAR(150) NOT NULL,
    ubicacion   VARCHAR(255),
    latitud     NUMERIC(9,6),
    longitud    NUMERIC(9,6),
    fecha_alta  TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE nodo_iot (
    id                SERIAL PRIMARY KEY,
    invernadero_id    INTEGER NOT NULL REFERENCES invernadero(id),
    identificador     VARCHAR(100) NOT NULL UNIQUE,
    version_firmware  VARCHAR(50),
    estado            VARCHAR(30) NOT NULL DEFAULT 'ACTIVO',
    ultima_conexion   TIMESTAMP,
    fecha_alta        TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE sensor (
    id                        SERIAL PRIMARY KEY,
    nodo_id                   INTEGER NOT NULL REFERENCES nodo_iot(id),
    tipo_id                   INTEGER NOT NULL REFERENCES tipo_sensor(id),
    unidad_medida_id          INTEGER REFERENCES unidad_medida(id),
    modelo                    VARCHAR(100),
    fecha_ultima_calibracion  TIMESTAMP
);

CREATE TABLE actuador (
    id       SERIAL PRIMARY KEY,
    nodo_id  INTEGER NOT NULL REFERENCES nodo_iot(id),
    tipo_id  INTEGER NOT NULL REFERENCES tipo_actuador(id),
    modelo   VARCHAR(100)
);

CREATE TABLE calibracion_sensor (
    id                SERIAL PRIMARY KEY,
    sensor_id         INTEGER NOT NULL REFERENCES sensor(id),
    usuario_id        INTEGER NOT NULL REFERENCES usuario(id),
    fecha             TIMESTAMP NOT NULL DEFAULT NOW(),
    valor_referencia  NUMERIC(10,4),
    observaciones     TEXT
);

CREATE TABLE dato_meteorologico (
    id                   SERIAL PRIMARY KEY,
    invernadero_id       INTEGER NOT NULL REFERENCES invernadero(id),
    fecha_hora           TIMESTAMP NOT NULL,
    temperatura_externa  NUMERIC(5,2),
    humedad_externa      NUMERIC(5,2),
    fecha_hora_audit     TIMESTAMP NOT NULL DEFAULT NOW()
);


--------------------------------------------------------------
-- CULTIVO

CREATE TABLE lote (
    id                   SERIAL PRIMARY KEY,
    invernadero_id       INTEGER NOT NULL REFERENCES invernadero(id),
    nodo_id              INTEGER NOT NULL REFERENCES nodo_iot(id) UNIQUE, -- 1 nodo = 1 lote
    usuario_id           INTEGER NOT NULL REFERENCES usuario(id),
    tipo_cultivo_id      INTEGER NOT NULL REFERENCES tipo_cultivo(id),
    etapa_fenologica_id  INTEGER REFERENCES etapa_fenologica(id),
    fecha_siembra        DATE NOT NULL,
    estado_id            INTEGER NOT NULL REFERENCES estado_lote(id)
);

CREATE TABLE planta (
    id                      SERIAL PRIMARY KEY,
    lote_id                 INTEGER NOT NULL REFERENCES lote(id),
    fila                    INTEGER NOT NULL,
    numero                  INTEGER NOT NULL,
    baby_leaf               BOOLEAN NOT NULL DEFAULT FALSE,
    fecha_cosecha_estimada  DATE,
    fecha_cosecha_real      DATE,
    UNIQUE (lote_id, fila, numero)
);


--------------------------------------------------------------
-- IoT - LECTURAS Y ACCIONES

CREATE TABLE lectura_sensor (
    id                SERIAL PRIMARY KEY,
    sensor_id         INTEGER NOT NULL REFERENCES sensor(id),
    lote_id           INTEGER NOT NULL REFERENCES lote(id),
    valor             NUMERIC(12,4) NOT NULL,
    unidad_medida_id  INTEGER REFERENCES unidad_medida(id),
    fecha_hora        TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE accion_control (
    id                  SERIAL PRIMARY KEY,
    actuador_id         INTEGER NOT NULL REFERENCES actuador(id),
    lote_id             INTEGER NOT NULL REFERENCES lote(id),
    lectura_disparo_id  INTEGER REFERENCES lectura_sensor(id),
    tipo_accion_id      INTEGER NOT NULL REFERENCES tipo_actuador(id),
    valor_aplicado      NUMERIC(10,4),
    fecha_hora          TIMESTAMP NOT NULL DEFAULT NOW()
);


--------------------------------------------------------------
-- IMÁGENES Y VISIÓN ARTIFICIAL
-- Siempre asociadas a una planta específica

CREATE TABLE imagen (
    id             SERIAL PRIMARY KEY,
    nodo_id        INTEGER NOT NULL REFERENCES nodo_iot(id),
    planta_id      INTEGER NOT NULL REFERENCES planta(id),  -- NOT NULL: siempre de una planta
    fecha_captura  TIMESTAMP NOT NULL DEFAULT NOW(),
    ruta_archivo   TEXT NOT NULL,
    tamano_bytes   INTEGER,
    ancho          INTEGER,
    alto           INTEGER
);

CREATE TABLE analisis_imagen (
    id                   SERIAL PRIMARY KEY,
    imagen_id            INTEGER NOT NULL REFERENCES imagen(id),
    etapa_fenologica_id  INTEGER REFERENCES etapa_fenologica(id),
    fecha_analisis       TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE deteccion_anomalia (
    id                SERIAL PRIMARY KEY,
    imagen_id         INTEGER NOT NULL REFERENCES imagen(id),
    tipo_anomalia_id  INTEGER NOT NULL REFERENCES tipo_anomalia(id),
    lote_id           INTEGER NOT NULL REFERENCES lote(id),
    confianza         NUMERIC(5,4),
    severidad_id      INTEGER REFERENCES severidad(id),
    fecha_deteccion   TIMESTAMP NOT NULL DEFAULT NOW()
);


--------------------------------------------------------------
-- ALERTAS Y NOTIFICACIONES

CREATE TABLE alerta (
    id                SERIAL PRIMARY KEY,
    tipo_alerta_id    INTEGER NOT NULL REFERENCES tipo_alerta(id),
    lote_id           INTEGER REFERENCES lote(id),
    nodo_id           INTEGER REFERENCES nodo_iot(id),
    severidad_id      INTEGER REFERENCES severidad(id),
    mensaje           TEXT,
    fecha_generacion  TIMESTAMP NOT NULL DEFAULT NOW(),
    estado            VARCHAR(30) NOT NULL DEFAULT 'PENDIENTE'
);

CREATE TABLE notificacion (
    id            SERIAL PRIMARY KEY,
    alerta_id     INTEGER NOT NULL REFERENCES alerta(id),
    usuario_id    INTEGER NOT NULL REFERENCES usuario(id),
    canal         VARCHAR(50),
    fecha_envio   TIMESTAMP,
    estado_envio  VARCHAR(30) NOT NULL DEFAULT 'PENDIENTE'
);

CREATE TABLE configuracion_notificacion (
    id              SERIAL PRIMARY KEY,
    usuario_id      INTEGER NOT NULL REFERENCES usuario(id),
    tipo_alerta_id  INTEGER NOT NULL REFERENCES tipo_alerta(id),
    habilitada      BOOLEAN NOT NULL DEFAULT TRUE,
    UNIQUE (usuario_id, tipo_alerta_id)
);


--------------------------------------------------------------
-- NUTRIENTES E INSUMOS

CREATE TABLE receta_nutriente (
    id                   SERIAL PRIMARY KEY,
    lote_id              INTEGER NOT NULL REFERENCES lote(id),
    etapa_fenologica_id  INTEGER REFERENCES etapa_fenologica(id),
    fecha_generacion     TIMESTAMP NOT NULL DEFAULT NOW(),
    descripcion          TEXT,
    estado               VARCHAR(30) NOT NULL DEFAULT 'ACTIVA'
);

CREATE TABLE receta_detalle (
    id         SERIAL PRIMARY KEY,
    receta_id  INTEGER NOT NULL REFERENCES receta_nutriente(id),
    insumo_id  INTEGER NOT NULL REFERENCES insumo(id),
    cantidad   NUMERIC(10,4) NOT NULL,
    unidad     VARCHAR(30)
);

CREATE TABLE consumo_insumo (
    id         SERIAL PRIMARY KEY,
    lote_id    INTEGER NOT NULL REFERENCES lote(id),
    insumo_id  INTEGER NOT NULL REFERENCES insumo(id),
    cantidad   NUMERIC(10,4) NOT NULL,
    costo      NUMERIC(10,2),
    fecha      TIMESTAMP NOT NULL DEFAULT NOW()
);


--------------------------------------------------------------
-- PREDICCIONES — a nivel planta
-- SIMULACIONES — a nivel lote / tipo_cultivo

CREATE TABLE prediccion (
    id                      SERIAL PRIMARY KEY,
    planta_id               INTEGER NOT NULL REFERENCES planta(id),  -- a nivel planta
    fecha_generacion        TIMESTAMP NOT NULL DEFAULT NOW(),
    fecha_estimada_cosecha  DATE,
    gdd_acumulado           NUMERIC(8,2),
    rendimiento_estimado    NUMERIC(8,2),
    version_modelo          VARCHAR(50),
    parametros_entrada      JSONB
);

CREATE TABLE simulacion (
    id                        SERIAL PRIMARY KEY,
    lote_id                   INTEGER REFERENCES lote(id),
    tipo_cultivo_id           INTEGER REFERENCES tipo_cultivo(id),
    usuario_id                INTEGER NOT NULL REFERENCES usuario(id),
    tipo                      VARCHAR(50),
    fecha                     TIMESTAMP NOT NULL DEFAULT NOW(),
    parametros_entrada        JSONB,
    resultado                 JSONB,
    costo_total_estimado      NUMERIC(12,2),
    fecha_estimada_resultado  DATE
);


--------------------------------------------------------------
-- ÍNDICES

CREATE INDEX idx_lectura_sensor_fecha   ON lectura_sensor(fecha_hora);
CREATE INDEX idx_lectura_sensor_sensor  ON lectura_sensor(sensor_id);
CREATE INDEX idx_lectura_sensor_lote    ON lectura_sensor(lote_id);
CREATE INDEX idx_alerta_estado          ON alerta(estado);
CREATE INDEX idx_alerta_lote            ON alerta(lote_id);
CREATE INDEX idx_imagen_planta          ON imagen(planta_id);
CREATE INDEX idx_imagen_nodo            ON imagen(nodo_id);
CREATE INDEX idx_prediccion_planta      ON prediccion(planta_id);
CREATE INDEX idx_deteccion_lote         ON deteccion_anomalia(lote_id);
CREATE INDEX idx_log_auditoria_usuario  ON log_auditoria(usuario_id);
CREATE INDEX idx_log_auditoria_fecha    ON log_auditoria(fecha_hora);
CREATE INDEX idx_planta_lote            ON planta(lote_id);
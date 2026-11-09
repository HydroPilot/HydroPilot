# Banco de Imágenes de Prueba - HydroPilot Visión Artificial

Esta carpeta contiene imágenes de prueba organizadas para validar el clasificador ONNX (`lettuce_npk_mobilenetv3.onnx`) en HydroPilot (localmente o en `/admin/mantenimiento`).

## Estructura de categorías

### 1. `sana_control/`
- **Etiqueta esperada**: `FN` (Full Nutrition / Nutrición Completa)
- **Características visuales**: Hojas de verde uniforme, sin clorosis, sin manchas necróticas y con desarrollo equilibrado.

### 2. `deficiencia_nitrogeno/`
- **Etiqueta esperada**: `-N` (Deficiencia de Nitrógeno)
- **Características visuales**: Clorosis generalizada. El amarillamiento comienza en hojas viejas/bajas y se extiende hacia el canope.

### 3. `deficiencia_fosforo/`
- **Etiqueta esperada**: `-P` (Deficiencia de Fósforo)
- **Características visuales**: Crecimiento lento o atrofiado, hojas con pigmentaciones oscuras, tonos azulados o púrpuras.

### 4. `deficiencia_potasio/`
- **Etiqueta esperada**: `-K` (Deficiencia de Potasio)
- **Características visuales**: Clorosis marginal y necrosis en los bordes y puntas de las hojas (aspecto de quemado en bordes).

## Cómo probar una imagen desde la terminal
```bash
/home/nix/utn/proyecto/.venv/bin/python3 /home/nix/utn/proyecto/temp-scripts/test_onnx_inference.py
```
(Modificar la ruta de `TEST_IMG` en el script para apuntar a cualquiera de estas imágenes).

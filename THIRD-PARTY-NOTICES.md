# Сторонние компоненты

| Компонент | Назначение | Источник / лицензия |
| :--- | :--- | :--- |
| NAudio | WASAPI и аудиопотоки | [NAudio](https://github.com/naudio/NAudio), MIT |
| RVC | Опциональный голосовой движок | [RVC-Project](https://github.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI), MIT |
| PyTorch | Нейросетевое исполнение | [PyTorch](https://github.com/pytorch/pytorch/blob/main/LICENSE), BSD-style |
| FAISS | Опциональный поиск по индексу | [FAISS](https://github.com/facebookresearch/faiss), MIT |
| Python | Опциональный worker | [Python](https://www.python.org/doc/copyright/) |

RVC устанавливается отдельно; закреплённая версия и её лицензия сохраняются в runtime. Общие assets ContentVec / RMVPE имеют собственные условия: [источник assets](https://huggingface.co/lj1995/VoiceConversionWebUI). Импортируемый голос не получает лицензию приложения автоматически. Используйте модели и записи, на которые у вас есть права.

Веса моделей, индексы, виртуальный аудиодрайвер и Python-пакеты не входят в этот репозиторий. При распространении сборки сохраняйте LICENSE / NOTICE всех включённых компонентов.

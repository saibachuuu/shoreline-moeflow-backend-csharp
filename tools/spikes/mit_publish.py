"""MIT bridge spike: publish task messages in exactly the Python-side wire format.

Mirrors shoreline-moeflow-backend wiring:
  app/tasks/mit.py      -> task names tasks.mit.detect_text / tasks.mit.ocr /
                           tasks.mit.translate (+ tasks.preprocess_mit)
  app/factory.py:147    -> task_routes ("tasks.mit.*", {"queue": "mit"})
  app/config.py:157-163 -> broker is an amqp:// URL (CELERY_BROKER_URL)

Uses celery.send_task so no task implementation is needed on this side --
the real implementation lives in the external manga-image-translator worker
(manga_translator/moeflow_worker.py), see docs/mit-bridge-spike.md.

Usage (broker URL has no real credentials; throwaway local container only):
  MIT_SPIKE_BROKER_URL=amqp://guest:guest@r2-mit-rmq-33070:5672// \\
      python tools/spikes/mit_publish.py
"""
import json
import os
import sys

from celery import Celery

BROKER_URL = os.environ.get(
    "MIT_SPIKE_BROKER_URL", "amqp://guest:guest@127.0.0.1:33070//"
)

app = Celery("mit_spike", broker=BROKER_URL)
# Same routing as app/factory.py:147 ("tasks.mit.*", {"queue": "mit"}),
# expressed as a dict (celery MapRoute form; identical routing outcome).
app.conf.task_routes = {"tasks.mit.*": {"queue": "mit"}}
app.conf.task_default_queue = "default"


def main() -> int:
    # Shape mirrors MitImageTaskApi.post (app/apis/manga_image_translator.py:50-72):
    # queue_task(mit_detect_text, tmpfile_path, **merged_default_params)
    detect_id = app.send_task(
        "tasks.mit.detect_text",
        args=["/MIT_STORAGE_ROOT/spike_page.png"],
        kwargs={
            "detector_key": "default",
            "detect_size": 2560,
            "text_threshold": 0.5,
            "box_threshold": 0.7,
            "unclip_ratio": 2.3,
            "invert": False,
            "gamma_correct": False,
            "rotate": False,
            "verbose": True,
            "device": "cuda",  # gpu_options in app/tasks/mit.py:15
        },
        queue="mit",
    )

    # Shape mirrors MitTranslateTaskApi.post (lines 80-84):
    # queue_task(mit_translate, **task_params)
    translate_id = app.send_task(
        "tasks.mit.translate",
        args=[],
        kwargs={
            "query": "\u3053\u3093\u306b\u3061\u306f",
            "translator": "gpt3.5",
            "target_lang": "CHT",
        },
        queue="mit",
    )

    print(json.dumps({"detect_text_id": detect_id.id, "translate_id": translate_id.id}))
    return 0


if __name__ == "__main__":
    sys.exit(main())

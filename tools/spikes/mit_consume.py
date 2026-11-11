"""MIT bridge spike: dump raw Celery protocol v2 messages from the mit queue.

Reads with kombu (same version pinned by the Python backend: kombu==5.6.2),
prints each message's delivery_info / properties / headers / body as JSON,
then ACKs it so the throwaway queue stays clean.

Usage:
  MIT_SPIKE_BROKER_URL=amqp://guest:guest@r2-mit-rmq-33070:5672// \\
      python tools/spikes/mit_consume.py [max_messages]
"""
import json
import os
import sys

from kombu import Connection

BROKER_URL = os.environ.get(
    "MIT_SPIKE_BROKER_URL", "amqp://guest:guest@127.0.0.1:33070//"
)


def default(obj):
    return repr(obj)


def main(argv) -> int:
    max_messages = int(argv[1]) if len(argv) > 1 else 10
    seen = 0
    with Connection(BROKER_URL) as conn:
        with conn.SimpleQueue("mit") as sq:
            while seen < max_messages:
                try:
                    msg = sq.get(timeout=5)
                except Exception as exc:  # queue.Empty on timeout
                    print(f"timeout after {seen} message(s): {exc}")
                    break
                record = {
                    "delivery_info": msg.delivery_info,
                    "delivery_tag": str(msg.delivery_tag),
                    "content_type": msg.content_type,
                    "content_encoding": msg.content_encoding,
                    "headers": msg.headers,
                    "properties": msg.properties,
                    "body": msg.decode(),
                }
                print(json.dumps(record, indent=2, default=default, ensure_ascii=False))
                print("---")
                msg.ack()
                seen += 1
    print(f"consumed={seen}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

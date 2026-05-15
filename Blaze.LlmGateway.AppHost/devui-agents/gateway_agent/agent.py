"""A minimal Agent Framework agent that routes through Blaze.LlmGateway.

DevUI (the `devui` CLI) discovers this module because the enclosing folder
is passed on the command line and `__init__.py` exports `agent`.

The agent talks to the OpenAI-compatible endpoint exposed by the gateway
(`/v1/chat/completions`). AppHost injects:

    OPENAI_BASE_URL   -> http://<api>/v1
    OPENAI_API_KEY    -> sk-blaze-devui  (any non-empty string is fine;
                                           the gateway does not enforce auth yet)

The `model` value is not the upstream model name — the gateway's router
picks the real provider based on message content. Pass ``"codebrewRouter"``
(the virtual router model) or any known destination name.
"""
from __future__ import annotations

import os

from agent_framework import ChatAgent
from agent_framework.openai import OpenAIChatClient


_base_url = os.environ.get("OPENAI_BASE_URL", "http://localhost:5000/v1")
_api_key = os.environ.get("OPENAI_API_KEY", "sk-blaze-devui")
_model = os.environ.get("BLAZE_GATEWAY_MODEL", "codebrewRouter")

agent = ChatAgent(
    name="BlazeGateway",
    description=(
        "Chats through the Blaze.LlmGateway router. "
        "The gateway selects the real provider based on prompt content, "
        "availability, and the CodebrewRouter fallback rules."
    ),
    instructions=(
        "You are a helpful assistant served through the Blaze.LlmGateway "
        "routing proxy. Respond naturally and answer directly. Do not include "
        "hidden reasoning, planning notes, scratchpad text, or a thinking "
        "process in chat responses. The gateway decides which upstream "
        "provider to use."
    ),
    chat_client=OpenAIChatClient(
        base_url=_base_url,
        api_key=_api_key,
        model_id=_model,
    ),
)

# Optional, isolated CPU service. Supply a reviewed wheelhouse with the named
# build context laya-wheels; online dependency resolution is prohibited here.
FROM python:3.12-slim@sha256:f77ac9e44ae96ef2c90b8053ea08c31f8be030f824196b0ae4db6d462c84e51f AS build
ARG LAYA_COMMIT=6d942c92081fbc139e736bbd9ac0023223c29b7f
ARG MODEL_REVISION=55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851
RUN apt-get update && apt-get upgrade -y && apt-get install -y --no-install-recommends ca-certificates && rm -rf /var/lib/apt/lists/*
RUN python -m venv --without-pip /opt/laya
COPY modules/laya/requirements.lock /requirements.lock
COPY --from=laya-wheels / /wheelhouse/
RUN python -m pip --python /opt/laya install --no-index --find-links=/wheelhouse --require-hashes -r /requirements.lock
ENV PATH=/opt/laya/bin:$PATH HF_HOME=/models/hf
# Only checkpoint retrieval is online; executable dependencies are fully locked.
RUN python -c "from huggingface_hub import snapshot_download; snapshot_download('convaiinnovations/laya', revision='${MODEL_REVISION}', allow_patterns=['multilingual/*','multilingual/tokenizer/*','multilingual/encoder/*'])"
RUN python -c "import pathlib,hashlib,json; root=pathlib.Path('/models'); files=[{'path':str(p.relative_to(root)),'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(root.rglob('*')) if p.is_file()]; (root/'model-files.sha256.json').write_text(json.dumps(files,indent=2)+'\\n')"

FROM python:3.12-slim@sha256:f77ac9e44ae96ef2c90b8053ea08c31f8be030f824196b0ae4db6d462c84e51f
ARG LAYA_COMMIT=6d942c92081fbc139e736bbd9ac0023223c29b7f
ARG MODEL_REVISION=55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851
RUN apt-get update && apt-get upgrade -y && rm -rf /var/lib/apt/lists/* && python -m pip uninstall -y pip setuptools && useradd --uid 10001 --create-home k-lani-bot-laya
COPY --from=build /opt/laya /opt/laya
COPY --from=build /models /models
# Model weights and Hub metadata are public assets; the non-root runtime must also read cache files created with mode 0600.
RUN chmod -R a+rX /models
COPY modules/laya/entrypoint.sh /entrypoint.sh
COPY modules/laya/requirements.lock /opt/laya/requirements.lock
RUN chmod 755 /entrypoint.sh
ENV PATH=/opt/laya/bin:$PATH HF_HOME=/models/hf HF_HUB_OFFLINE=1 TRANSFORMERS_OFFLINE=1 \
    LAYA_REVISION=${MODEL_REVISION} LAYA_MODELS=multilingual LAYA_DEVICE=cpu \
    LAYA_PRELOAD=1 LAYA_MAX_LOADED=1 LAYA_THREADS=2 LAYA_MAX_CONCURRENT=2 LAYA_MAX_TOKEN_BUDGET=8192 \
    LAYA_HOST=0.0.0.0 LAYA_PORT=8000 LAYA_LOG_LEVEL=warning
LABEL org.opencontainers.image.source="https://github.com/NandhaKishorM/laya" \
      org.opencontainers.image.revision="${LAYA_COMMIT}" k-lani-bot.model-revision="${MODEL_REVISION}"
USER 10001:10001
EXPOSE 8000
ENTRYPOINT ["/entrypoint.sh"]

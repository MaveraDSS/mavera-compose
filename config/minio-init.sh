#!/bin/sh
# Creates the buckets the document/storage/pdf services write to.
set -eu

mc alias set local "http://minio:9000" "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"

for bucket in "$BUCKET_ASSETS" "$BUCKET_DOCUMENTS"; do
    if mc ls "local/$bucket" >/dev/null 2>&1; then
        echo "bucket already exists: $bucket"
    else
        mc mb "local/$bucket"
        echo "created bucket: $bucket"
    fi
done

# The services generate presigned URLs, so public policies are not required.
# Uncomment if you want to fetch objects directly from a browser during debugging.
# mc anonymous set download "local/$BUCKET_ASSETS"

echo "minio-init complete"

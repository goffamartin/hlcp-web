#!/bin/sh
set -eu
# Certbot deploy hook: runs only after a certificate was successfully renewed.
/usr/sbin/nginx -t
/usr/bin/systemctl reload nginx

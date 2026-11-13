#!/bin/sh
set -eu
case "${LEARNING_LABS:-false}" in true) labs=true ;; *) labs=false ;; esac
printf '{"ApiBaseUrl":"/","Features":{"LearningLabs":%s}}\n' "$labs" > /usr/share/nginx/html/appsettings.json

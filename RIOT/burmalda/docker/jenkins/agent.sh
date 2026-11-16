#!/bin/sh
set -eu
curl --fail --silent --show-error --retry 5 http://jenkins:8080/jnlpJars/agent.jar -o /tmp/agent.jar
exec java -jar /tmp/agent.jar "$@"

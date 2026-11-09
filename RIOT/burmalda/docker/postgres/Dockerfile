FROM postgres:17-alpine
COPY docker/postgres/init-databases.sh /docker-entrypoint-initdb.d/10-init-databases.sh
COPY docker/postgres/replication/ /opt/replication/
RUN sed -i 's/\r$//' /docker-entrypoint-initdb.d/*.sh /opt/replication/*.sh

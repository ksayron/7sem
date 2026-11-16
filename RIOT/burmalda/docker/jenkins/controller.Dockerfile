FROM jenkins/jenkins:2.555.2-lts-jdk21@sha256:01c992ffef29dcf41c7164e8c16285c657c2368c4943dda8d68c93fdf54447d5
COPY --from=plugins / /usr/share/jenkins/ref/plugins/
COPY docker/jenkins/init.groovy /usr/share/jenkins/ref/init.groovy.d/burmalda.groovy

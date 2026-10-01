FROM mono:6.12
WORKDIR /app
COPY server.cs index.html og.txt stats.html ./
RUN mcs -codepage:utf8 -r:System.Web.Extensions.dll -out:Copometro.exe server.cs
ENV PORT=10000
EXPOSE 10000
CMD ["mono", "Copometro.exe"]

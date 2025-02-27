# iviva Load Testing Tool

A simple tool to load test an iviva installation.

Uses a scenario file (in json format) to simulate a user going through scenarios.

```
IvivaLoadTester --url "https://my.iviva.cloud" --key "my-api-key" -s "scenario.json" -c 50
```

This will run a load test with 50 concurrent users running the scenario.json sequence.


You can create a scenario file by using a browser to do some work and then exporting a har file.
The tool has a built-in har file converter that can generate a scenario json file

```
IvivaLoadTester --convert-har <har-file.har> -o <scenario.json>
```
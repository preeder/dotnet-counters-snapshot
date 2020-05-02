
# dotnet-counters-snapshot

This tool fills the void in the very nice dotnet core 3.1 tool dotnet-counters by supplying the capability to gather a single snapshot of data from a set of counters.

This can then be used in a text pipeline to provide additional functionality.  For example, we use this bash script (fired each minute from cron) to update aws cloudwatch counters.


```bash
#!/bin/bash
# collects metrics for InventoryWeb service and sends to Cloudwatch<br />
<br />
# get PID
PID=`ps -ax | grep InventoryWeb | grep -v grep | sed -e 's/^ *//' -e 's/ .*//'`

# collect metrics
dotnet-counters-snapshot snapshot -p $PID -m 10000 System.Runtime[cpu-usage,working-set] \
   InventoryImport.Metrics[queued,processed,errors,queue-time,processing-time,total-time,slots-available] \
   | sed -e 's/System.Runtime\//VehicleInventory\/System /' -e 's/InventoryImport.Metrics\//VehicleInventory\/Imports /' \
   | while IFS= read line; do
        #split it
        atoms=($(echo "$line"))
        name=${atoms[0]}
        metric=${atoms[1]}
        value=${atoms[2]}

        aws cloudwatch put-metric-data --namespace $name --metric-name=$metric --value $value
        echo `date "+%Y%m%d %H%M%S"` $name::$metric $value >> /opt/vehicleInventory/sendMetrics.log
done
```
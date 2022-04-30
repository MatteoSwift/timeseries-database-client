
## 时序数据访问接口说明

### 函数:snapshot 获取最新的测点数据,指定设备,指定测点集合

- snapshpt(device:string, timeseries:string[]): List<(tag:string, time:DateTime, value:number|string)>

### 函数:archive 获取原始归档数据,指定设备,测点,开始时间,结束时间,边界取值方式
- archive(device:string, timeseries:string, begin:DateTime, end:DateTime, boundary:"Auto"|"Inner"|"Outer"):List<(time:DateTime, value:number|string)>

    - boundary 含义说明
        + Auto 当begin或end时刻无数据时,不做任何数据处理
        + Inner 当begin时刻无数据时,则使用begin之后第一个值作为begin时刻的数据;当end时刻无数据时,则使用end之前的最后一个数据作为end时刻的数据
        + Outer 当begin时刻无数据时,则使用begin之前最后一个值作为begin时刻的数据;当end时刻无数据时,则使用end之后的最后一个数据作为end时刻的数据

##### 该函数是取值核心,boundary至关重要
示例说明:
```
 insert into root.demo(time,ts1,ts2,ts3) values (0,1.0,2,3)
 insert into root.demo(time,ts1,ts2,ts3) values (4,1.2,2,3)
 insert into root.demo(time,ts1,ts2,ts3) values (6,1.5,2,3)
 insert into root.demo(time,ts1,ts2,ts3) values (9,1.6,2,3)
 insert into root.demo(time,ts1,ts2,ts3) values (16,1.8,2,3)
 insert into root.demo(time,ts1,ts2,ts3) values (20,1.4,2,3)
```
- archie("demo", "ts1", 3, 15, "Auto") => [4:1.2, 6:1.5, 9:1.6] 必须在时间范围内有数据
- archie("demo", "ts1", 3, 15, "Inner") => [3:1.2, 4:1.2, 6:1.5, 9:1.6, 15:1.6] 时间边界无数数据则使用内部临近数据补充
- archie("demo", "ts1", 3, 15, "Outer") => [3:0, 4:1.2, 6:1.5, 9:1.6, 15:1.8] 时间边界无数数据则使用外部临近数据补充

`时序数据,必须结合实际场景才有数据的物理意义,由此才会有outer模式的边界处理意义,Outer的方式是核工业用户常用的读数据方式`


### 函数:history 获取历史数据, 与archive的区别是 history 允许差值计算
- history(device:string, timeseries:string, begin:DateTime, end:DateTime, sample:number|string, mode:"before"|"after"|"computation"):List<(time:DateTime, value:number|string)>

        - sample 采样间隔,单位毫秒,或是指定单位,如10m 
        - mode 计算模式
            + before 向前取值, 当begin+(n*sample)时刻没有数据时则使用该时刻之前的最后一个数值
            + after 向前取值, 当begin+(n*sample)时刻没有数据时则使用该时刻之后的第一个数值
            + computation 线性差值, 当begin+(n*sample)时刻没有数据时使用该时刻前后临近的2个数据线性方程计算差值y=kx+b
        
`sample=1ms,mode=before 相当于已有的 select last_value(ts1) from root.demo group by ([3,15],1ms) fill(double(previous))`
 computation主要是用于模拟量数据,在物理过程中它的数值变化是连续的

- historyx(device:string, timeseries:string[], begin:DateTime, end:DateTime, sample:number|string, mode:"before"|"after"|"computation"):List<(time:DateTime, values:number[])>
  
  用于数据导出,或是业务层抽取数据之后传给算法逻辑处理, 这样的数据比较容易形成参数矩阵(相当于csv数据),要求等间隔数据采样

### 函数:summary 获取统计数据, 返回 (max, min, avg, total)
- summary(device:string, timeseries:string, begin:DateTime, end:DateTime):(max:number, min:number, avg:number, total:number)

    - max => 等同 max(ts1)
    - min => 等同 min(ts1)
    - avg => 等同 avg(ts1)
    - total => 等同 sum(ts1)

### 函数:plot 获取绘图数据
- plot(device:string, timeseries:string, begin:DateTime, end:DateTime, pixel:number):List<(time:DateTime, value:number)>

    - pixel 页面UI像素

主要用于图形化趋势分析使用,前端页面要快速显示1天或1月的温度和功率曲线,数据存储都是1-2秒级别, 1日可达8万行数据,而前端页面这个Chart像素1440px, 显然像素1440换算成1440个数据即可满足前端绘图需求.
如果将8万*n天数据返回给页面必然浏览器崩溃,
通过查询接口将数据从iotdb服务端传输到应用服务器之后有业务后台服务降采样,也比较消耗网络带宽传输无意义数据
因此需要尽可能的在底层实现降采样处理
比如 将 1天划分为 1440 个片段,每个片段内得到 几个特征数据,包括(min和max),1-2个最接近(avg值)的(time,value),这样得到3-4个特征数据,然后合并作为1日的plot趋势数据

